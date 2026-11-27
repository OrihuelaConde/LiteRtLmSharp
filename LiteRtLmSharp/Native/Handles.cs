using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

/// <summary>
/// Base <see cref="SafeHandle"/> for LiteRT-LM opaque pointers. Each derived type
/// frees its native object via the matching <c>litert_lm_*_delete</c> function.
/// </summary>
internal abstract class LiteRtLmHandle : SafeHandle
{
    protected LiteRtLmHandle(nint handle) : base(invalidHandleValue: nint.Zero, ownsHandle: true)
        => SetHandle(handle);

    public override bool IsInvalid => handle == nint.Zero;

    /// <summary>The raw pointer, for passing to native calls that don't take ownership.</summary>
    internal nint Ptr => handle;
}

internal sealed class EngineSettingsHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_engine_settings_delete(handle);
        return true;
    }
}

/// <summary>The outcome of <see cref="EngineLiveness.TryAcquire"/>.</summary>
internal enum EngineAdmission
{
    /// <summary>The engine took a slot.</summary>
    Admitted,
    /// <summary>Refused: engines are alive and the engine being loaded did not opt in.</summary>
    NewEngineNotOptedIn,
    /// <summary>Refused: a live engine did not opt in, so nothing may load next to it.</summary>
    LiveEngineNotOptedIn,
}

/// <summary>
/// The process-wide gate for live chat engines. A slot is acquired in <see cref="LiteRtEngine.Load"/> and
/// released when the native engine is actually destroyed, in <see cref="EngineHandle.ReleaseHandle"/> —
/// which runs on Dispose AND on finalization, so a GC'd-but-undisposed engine still frees its slot (and
/// only after the native engine is gone).
/// </summary>
/// <remarks>
/// By default one engine may be alive at a time. Engines loaded with
/// <see cref="LiteRtEngineOptions.AllowMultipleEngines"/> may coexist with each other, but never with an
/// engine that did not opt in, in either order. The live count and the count of engines that did not opt in
/// change together under one lock (two counters cannot be checked and updated atomically with CAS), and
/// neither goes below zero. <see cref="Default"/> is the gate <see cref="LiteRtEngine.Load"/> uses; tests
/// create their own instances instead of draining process-global state.
/// </remarks>
internal sealed class EngineLiveness
{
    /// <summary>The process-wide gate.</summary>
    public static EngineLiveness Default { get; } = new();

    private readonly object _lock = new();
    private int _live;
    private int _exclusive;

    /// <summary>How many chat engines are currently alive.</summary>
    public int LiveCount
    {
        get { lock (_lock) return _live; }
    }

    /// <summary>Takes an engine slot when the rule allows it.</summary>
    /// <param name="allowMultiple">Whether the engine being loaded opted in to coexisting.</param>
    public EngineAdmission TryAcquire(bool allowMultiple)
    {
        lock (_lock)
        {
            if (_live > 0)
            {
                if (!allowMultiple)
                    return EngineAdmission.NewEngineNotOptedIn;
                if (_exclusive > 0)
                    return EngineAdmission.LiveEngineNotOptedIn;
            }
            _live++;
            if (!allowMultiple)
                _exclusive++;
            return EngineAdmission.Admitted;
        }
    }

    /// <summary>Frees the slot of an engine that was admitted with the same <paramref name="allowMultiple"/>
    /// value (never below zero).</summary>
    public void Release(bool allowMultiple)
    {
        lock (_lock)
        {
            if (_live <= 0)
                return;
            _live--;
            if (!allowMultiple && _exclusive > 0)
                _exclusive--;
        }
    }
}

/// <summary>The native chat engine. <paramref name="modelPathKey"/> is the key
/// <see cref="LoadedModelPaths.Add"/> returned for the engine's model file, or <c>null</c> when nothing was
/// counted.</summary>
internal sealed class EngineHandle(nint handle, bool allowMultiple, string? modelPathKey = null) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_engine_delete(handle);
        // Free this engine's slot only after the native engine is destroyed (ordering matters: a new
        // engine must not be created while this one's native object still exists). This runs once the
        // engine is disposed or finalized AND every ConversationHandle and SessionHandle released its
        // reference, so a forgotten/GC'd engine still frees its slot, and never while one of its native
        // conversations or sessions is alive. The
        // handle carries the engine's opt-in because a finalizer must not depend on other managed objects.
        EngineLiveness.Default.Release(allowMultiple);
        // The model file stops counting as held only once the native engine is gone.
        if (modelPathKey is not null)
            LoadedModelPaths.Default.Release(modelPathKey);
        return true;
    }
}

internal sealed class SessionConfigHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_session_config_delete(handle);
        return true;
    }
}

internal sealed class ConversationConfigHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_conversation_config_delete(handle);
        return true;
    }
}

/// <summary>A native conversation. It holds a reference on its engine's handle, taken by the caller with
/// <see cref="SafeHandle.DangerousAddRef"/> before the native create or clone, and releases it after deleting
/// the conversation: a native conversation uses its engine until it is deleted, so the engine is deleted only
/// after its last conversation, whichever of them is disposed or finalized first.</summary>
internal sealed class ConversationHandle(nint handle, EngineHandle engine) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_conversation_delete(handle);
        engine.DangerousRelease();
        return true;
    }
}

/// <summary>A native session. Like <see cref="ConversationHandle"/>, it holds a reference on its engine's
/// handle, taken by the caller with <see cref="SafeHandle.DangerousAddRef"/> before the native create, and
/// releases it after deleting the session, so the engine is deleted only after its last session.</summary>
/// <remarks>It also keeps the <see cref="GCHandle"/> of every decode stream that has settled
/// (<see cref="Retire"/>) and frees them only after <c>litert_lm_session_delete</c> returns. C API 1.1.0 has no
/// session call that waits for a stream's last callback, so a stream's callback data stays valid until the
/// native session is gone. A stream retires its handle while it still holds its reference on this handle, so
/// the retire always comes before the release. A stream clears its handle's target before it retires it, so the
/// retired handles keep their slots but root no settled stream.</remarks>
internal sealed class SessionHandle(nint handle, EngineHandle engine) : LiteRtLmHandle(handle)
{
    private readonly object _retiredLock = new();
    private List<GCHandle>? _retired;
    private bool _deleted;

    /// <summary>Keeps <paramref name="gcHandle"/> until the native session is deleted, then frees it. When the
    /// session is already deleted, frees it at once: the delete has returned, and no stream callback was observed
    /// after a delete (LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and GPU).</summary>
    internal void Retire(GCHandle gcHandle)
    {
        lock (_retiredLock)
        {
            if (!_deleted)
            {
                (_retired ??= []).Add(gcHandle);
                return;
            }
        }
        if (gcHandle.IsAllocated)
            gcHandle.Free();
    }

    /// <summary>The number of handles waiting for the native delete, for tests.</summary>
    internal int RetiredCount
    {
        get { lock (_retiredLock) return _retired?.Count ?? 0; }
    }

    protected override bool ReleaseHandle()
    {
        // Delete first: a stream's callback data must stay valid until the native session is gone. The engine
        // reference goes last, because a session must be deleted before its engine.
        LiteRtLmNative.litert_lm_session_delete(handle);
        List<GCHandle>? retired;
        lock (_retiredLock)
        {
            _deleted = true;
            retired = _retired;
            _retired = null;
        }
        if (retired is not null)
        {
            foreach (GCHandle gcHandle in retired)
            {
                if (gcHandle.IsAllocated)
                    gcHandle.Free();
            }
        }
        engine.DangerousRelease();
        return true;
    }
}

internal sealed class ConversationOptionalArgsHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_conversation_optional_args_delete(handle);
        return true;
    }
}

internal sealed class JsonResponseHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_json_response_delete(handle);
        return true;
    }
}

internal sealed class BenchmarkInfoHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_benchmark_info_delete(handle);
        return true;
    }
}

internal sealed class TokenizeResultHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_tokenize_result_delete(handle);
        return true;
    }
}

internal sealed class DetokenizeResultHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_detokenize_result_delete(handle);
        return true;
    }
}

/// <summary>A token union the caller owns, such as the start token. Never wraps an element borrowed from a
/// <see cref="TokenUnionsHandle"/> collection: the collection owns and deletes those.</summary>
internal sealed class TokenUnionHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_token_union_delete(handle);
        return true;
    }
}

internal sealed class TokenUnionsHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_token_unions_delete(handle);
        return true;
    }
}

internal sealed class EmbeddingEngineSettingsHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_embedding_engine_settings_delete(handle);
        return true;
    }
}

/// <summary>The native embedding engine. Unlike <see cref="EngineHandle"/> it takes no part in the
/// one-live-engine gate (<see cref="EngineLiveness"/>): an embedding engine coexists with a chat engine, and
/// releasing it must never free the chat engine's slot. <paramref name="modelPathKey"/> is the key
/// <see cref="LoadedModelPaths.Add"/> returned for the engine's model file, or <c>null</c> when nothing was
/// counted.</summary>
internal sealed class EmbeddingEngineHandle(nint handle, string? modelPathKey = null) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_embedding_engine_delete(handle);
        if (modelPathKey is not null)
            LoadedModelPaths.Default.Release(modelPathKey);
        return true;
    }
}

internal sealed class EmbeddingOptionsHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_embedding_options_delete(handle);
        return true;
    }
}

/// <summary>A response the caller owns (from <c>compute_embedding</c>); never a response lent by a
/// collection through <c>litert_lm_embedding_responses_get_at</c>, which the collection frees.</summary>
internal sealed class EmbeddingResponseHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_embedding_response_delete(handle);
        return true;
    }
}

internal sealed class EmbeddingResponsesHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_embedding_responses_delete(handle);
        return true;
    }
}

internal sealed class InputDataHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_input_data_delete(handle);
        return true;
    }
}

internal sealed class LoadedFileHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_loaded_file_delete(handle);
        return true;
    }
}

/// <summary>Model sync options. Each sync call creates its own and releases it when the call ends.</summary>
internal sealed class ModelSyncOptionsHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_model_sync_options_delete(handle);
        return true;
    }
}

/// <summary>A model sync plan. It owns its summary string, so the summary is copied before the release.</summary>
internal sealed class ModelSyncPlanHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_model_sync_plan_delete(handle);
        return true;
    }
}

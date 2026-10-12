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

/// <summary>
/// Process-wide gate for the "only one live engine at a time" rule. The slot is acquired in
/// <see cref="LiteRtEngine.Load"/> and released when the native engine is actually destroyed, in
/// <see cref="EngineHandle.ReleaseHandle"/> — which runs on Dispose AND on finalization, so a
/// GC'd-but-undisposed engine still frees the slot (and only after the native engine is gone).
/// </summary>
/// <remarks>
/// <see cref="AllowMultiple"/> lifts the rule (opt-in, see <see cref="LiteRtEngine.AllowMultipleEngines"/>):
/// the counter then tracks how many engines are alive instead of being a 0/1 slot. Either way the counter
/// never goes negative, so a double dispose or a finalizer/dispose race cannot corrupt it.
/// </remarks>
internal static class EngineLiveness
{
    private static int s_live;
    private static volatile bool s_allowMultiple;

    /// <summary>Whether any number of engines may be alive at once (off by default).</summary>
    public static bool AllowMultiple
    {
        get => s_allowMultiple;
        set => s_allowMultiple = value;
    }

    /// <summary>How many engines are currently alive (0 with the single-engine rule in force).</summary>
    public static int LiveCount => Volatile.Read(ref s_live);

    /// <summary>Takes an engine slot: always succeeds in multi-engine mode, otherwise only when no engine
    /// is live.</summary>
    public static bool TryAcquire()
    {
        if (s_allowMultiple)
        {
            Interlocked.Increment(ref s_live);
            return true;
        }
        return Interlocked.CompareExchange(ref s_live, 1, 0) == 0;
    }

    /// <summary>Frees an engine slot (never below zero).</summary>
    public static void Release()
    {
        int current;
        do
        {
            current = Volatile.Read(ref s_live);
            if (current <= 0)
                return;
        }
        while (Interlocked.CompareExchange(ref s_live, current - 1, current) != current);
    }
}

internal sealed class EngineHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_engine_delete(handle);
        // Free this engine's slot only after the native engine is destroyed (ordering matters: a new
        // engine must not be created while this one's native object still exists). This runs on Dispose
        // and on finalization, so even a forgotten/GC'd engine releases the slot — but note that every
        // LiteRtConversation strongly references its engine (for the KV overflow guard), so finalization
        // can only happen once no conversation objects remain reachable either.
        EngineLiveness.Release();
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

internal sealed class ConversationHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_conversation_delete(handle);
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
/// releasing it must never free the chat engine's slot.</summary>
internal sealed class EmbeddingEngineHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_embedding_engine_delete(handle);
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

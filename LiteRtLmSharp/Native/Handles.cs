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
internal static class EngineLiveness
{
    private static int s_live;

    /// <summary>Tries to take the single engine slot; returns false if one is already live.</summary>
    public static bool TryAcquire() => Interlocked.CompareExchange(ref s_live, 1, 0) == 0;

    /// <summary>Frees the engine slot.</summary>
    public static void Release() => Interlocked.Exchange(ref s_live, 0);
}

internal sealed class EngineHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_engine_delete(handle);
        // Free the one-engine slot only after the native engine is destroyed (ordering matters: a new
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

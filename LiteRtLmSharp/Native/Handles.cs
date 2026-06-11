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

internal sealed class EngineHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_engine_delete(handle);
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

internal sealed class JsonResponseHandle(nint handle) : LiteRtLmHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        LiteRtLmNative.litert_lm_json_response_delete(handle);
        return true;
    }
}

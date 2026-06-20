using System.Runtime.InteropServices;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>Whether a <see cref="LiteRtTokenUnion"/> carries a literal string or a sequence of token ids.</summary>
public enum LiteRtTokenKind
{
    /// <summary>The token is a literal string (e.g. <c>"&lt;eos&gt;"</c>); read <see cref="LiteRtTokenUnion.Text"/>.</summary>
    String = 0,

    /// <summary>The token is a sequence of token ids; read <see cref="LiteRtTokenUnion.Ids"/>.</summary>
    Ids = 1,
}

/// <summary>
/// A single model start (BOS) or stop (EOS) token, as reported by
/// <see cref="LiteRtEngine.GetStartToken"/> / <see cref="LiteRtEngine.GetStopTokens"/>. A model
/// expresses each such token either as a literal <see cref="Text"/> string or as a sequence of token
/// <see cref="Ids"/>; <see cref="Kind"/> says which. Use it to recognize when generated output reaches
/// a stop token, or to compare against <see cref="LiteRtEngine.Tokenize"/> output.
/// </summary>
public sealed class LiteRtTokenUnion
{
    private readonly int[] _ids;

    internal LiteRtTokenUnion(LiteRtTokenKind kind, string? text, int[] ids)
    {
        Kind = kind;
        Text = text;
        _ids = ids;
    }

    /// <summary>Whether this token is a <see cref="LiteRtTokenKind.String"/> or a set of <see cref="LiteRtTokenKind.Ids"/>.</summary>
    public LiteRtTokenKind Kind { get; }

    /// <summary>The literal string when <see cref="Kind"/> is <see cref="LiteRtTokenKind.String"/>; otherwise <c>null</c>.</summary>
    public string? Text { get; }

    /// <summary>The token ids when <see cref="Kind"/> is <see cref="LiteRtTokenKind.Ids"/>; otherwise empty.</summary>
    public IReadOnlyList<int> Ids => _ids;

    /// <summary>A readable form: the string itself, or the id list rendered as <c>[a, b, c]</c>.</summary>
    public override string ToString()
        => Kind == LiteRtTokenKind.String ? Text ?? string.Empty : $"[{string.Join(", ", _ids)}]";

    /// <summary>
    /// Reads a native <c>LiteRtLmTokenUnion</c> into a managed value, copying out its data. Does not take
    /// ownership of <paramref name="tokenUnion"/> — the caller still frees the native object.
    /// </summary>
    internal static unsafe LiteRtTokenUnion FromNative(nint tokenUnion)
    {
        LiteRtLmTokenUnionType type = LiteRtLmNative.litert_lm_token_union_get_type(tokenUnion);
        if (type == LiteRtLmTokenUnionType.Ids)
        {
            int* outTokens;
            nuint count;
            int rc = LiteRtLmNative.litert_lm_token_union_get_ids(tokenUnion, &outTokens, &count);
            if (rc != 0 || outTokens is null || count == 0)
                return new LiteRtTokenUnion(LiteRtTokenKind.Ids, text: null, []);

            var ids = new int[count];
            Marshal.Copy((nint)outTokens, ids, 0, checked((int)count));
            return new LiteRtTokenUnion(LiteRtTokenKind.Ids, text: null, ids);
        }

        nint strPtr = LiteRtLmNative.litert_lm_token_union_get_string(tokenUnion);
        return new LiteRtTokenUnion(LiteRtTokenKind.String, Marshal.PtrToStringUTF8(strPtr) ?? string.Empty, []);
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace LiteRtLmSharp.Native;

/// <summary>
/// Reads the LiteRT-LM C API's error report (native v0.18.0+, <c>c/error_reporter.h</c>): the canonical
/// status code and message that the last <i>failing</i> call left on the calling thread, so a failed native
/// call surfaces the runtime's own reason instead of a bare "returned null".
/// </summary>
/// <remarks>
/// The native contract is errno-style: the report is thread-local, it is set only on failure (a successful
/// call never clears it), and the message belongs to the library until the next failure or clear on that
/// thread. The binding therefore (1) clears the slot right before a call it may need to diagnose, so a
/// report can never be a stale one left by an earlier, unrelated failure; (2) decides failure from the
/// call's own return value, never from the report; and (3) reads the report on the same thread right after
/// the failing call, then clears it, which also frees the message on long-lived thread-pool threads. Every
/// diagnosed call site is synchronous on one thread: the blocking sends that the async APIs move to the
/// thread pool move there as a whole, read included.
/// </remarks>
internal static class NativeError
{
    /// <summary>Clears the calling thread's report before a call that may need diagnosing.</summary>
    internal static void Clear() => LiteRtLmNative.litert_lm_clear_last_error();

    /// <summary>Reads and clears the calling thread's report; <c>null</c> when the runtime recorded none.</summary>
    internal static (LiteRtStatusCode Code, string Message)? Take()
    {
        int code = LiteRtLmNative.litert_lm_get_last_error_code();
        string? message = Marshal.PtrToStringUTF8(LiteRtLmNative.litert_lm_get_last_error_message());
        LiteRtLmNative.litert_lm_clear_last_error();
        if (code == 0 && string.IsNullOrEmpty(message))
            return null;
        // The header freezes the canonical set but asks callers to read unrecognized values as Unknown, so a
        // binding built against this version stays correct with a newer native library.
        var status = Enum.IsDefined((LiteRtStatusCode)code) ? (LiteRtStatusCode)code : LiteRtStatusCode.Unknown;
        return (status, Detail(status, message));
    }

    /// <summary>
    /// Reads a status the runtime delivered as text, such as the error string of a stream chunk
    /// (<c>"INVALID_ARGUMENT: reason"</c>): the status from its leading canonical name, and the detail
    /// normalized like <see cref="Take"/>. <c>null</c> when the text starts with no canonical name.
    /// </summary>
    internal static (LiteRtStatusCode Code, string Message)? ParseStatusText(string text)
    {
        string trimmed = text.Trim();
        foreach (LiteRtStatusCode candidate in Enum.GetValues<LiteRtStatusCode>())
        {
            if (StartsWithName(trimmed, CanonicalName(candidate)))
                return (candidate, Detail(candidate, trimmed));
        }
        return null;
    }

    /// <summary>
    /// The runtime's reason without the leading code, as one line; empty when it gave none. The runtime
    /// stores the full absl status text (<c>"INVALID_ARGUMENT: Invalid magic number…"</c>), so the code is
    /// dropped to avoid printing it twice. With the native log silenced (a level above 5, such as 1000),
    /// LiteRT's status macros record no reason at all and the text is the bare <c>"INVALID_ARGUMENT: "</c>.
    /// </summary>
    internal static string Detail(LiteRtStatusCode status, string? message)
    {
        string text = (message ?? "").Trim();
        string name = CanonicalName(status);
        if (StartsWithName(text, name))
            text = text[name.Length..].TrimStart(':').Trim();
        // A dangling colon (a native message whose detail was empty) would read as "…read:." once the
        // sentence is closed.
        return Untrace(text).TrimEnd(':', ' ');
    }

    private static bool StartsWithName(string text, string name) =>
        text.StartsWith(name, StringComparison.Ordinal) && (text.Length == name.Length || text[name.Length] == ':');

    /// <summary>
    /// Rewrites a LiteRT status trace into one line: the reason first, then the innermost source location.
    /// </summary>
    /// <remarks>
    /// Errors raised through LiteRT's status macros arrive as a call trace: one <c>ERROR: [path:line]</c>
    /// location per line, the lines after the first prefixed with <c>└</c>, and the reason, when there is
    /// one, on the last line. A missing cache directory, for example, arrives as three lines
    /// (<c>embedding_engine_impl.cc:402</c>, <c>embedding_engine_settings.cc:316</c>, then
    /// <c>Cache directory does not exist or is not writable: …</c>) and reads as
    /// "Cache directory does not exist or is not writable: … (at embedding_engine_settings.cc:316)". A trace
    /// without a reason keeps only the innermost location. Other messages are returned unchanged.
    /// </remarks>
    internal static string Untrace(string message)
    {
        if (!message.Contains("ERROR: [", StringComparison.Ordinal))
            return message;
        string? location = null;
        var reason = new StringBuilder();
        foreach (string rawLine in message.Split('\n'))
        {
            string line = rawLine.Trim().TrimStart('└').Trim();
            if (line.StartsWith("ERROR: [", StringComparison.Ordinal) && line.IndexOf(']') is var end and > 0)
            {
                location = line[8..end];
                line = line[(end + 1)..].Trim();
            }
            if (line.Length > 0)
                reason.Append(reason.Length > 0 ? " " : "").Append(line);
        }
        if (location is null)
            return message;
        // The file and line are enough for a bug report; the repository path is noise.
        string where = location[(location.LastIndexOf('/') + 1)..];
        return reason.Length > 0 ? $"{reason.ToString().TrimEnd(':', '.', ' ')} (at {where})" : $"no details, failed at {where}";
    }

    /// <summary>
    /// Builds the exception for a failed native call. The message is <paramref name="failure"/> (what
    /// failed), then the runtime's status and message when it reported one; <paramref name="fallbackHint"/>
    /// only when it reported nothing (the binding's guess at the usual causes); and <paramref name="hint"/>
    /// in both cases (guidance that stays useful next to the runtime's reason).
    /// </summary>
    internal static LiteRtException Exception(string failure, string? hint = null, string? fallbackHint = null)
        => Build(failure, Take(), hint, fallbackHint);

    /// <summary>
    /// Builds the exception like <see cref="Exception(string, string?, string?)"/>, choosing the guidance
    /// from the runtime's report, for hints that only fit some causes. <paramref name="hintFor"/> receives
    /// <c>null</c> when the runtime gave no reason (no report, or a status without detail).
    /// </summary>
    internal static LiteRtException Exception(
        string failure, Func<(LiteRtStatusCode Code, string Message)?, string?> hintFor, string? fallbackHint = null)
    {
        var report = Take();
        return Build(failure, report, hintFor(HasReason(report) ? report : null), fallbackHint);
    }

    /// <summary>
    /// Builds the exception for a failure the runtime delivered as status text rather than through the
    /// thread report, such as a stream chunk's error, so it carries the same status, one-line reason and
    /// guidance as a failed blocking call.
    /// </summary>
    internal static LiteRtException FromStatusText(
        string failure, string statusText, Func<(LiteRtStatusCode Code, string Message)?, string?> hintFor)
    {
        if (ParseStatusText(statusText) is { } report)
            return Build(failure, report, hintFor(HasReason(report) ? report : null), fallbackHint: null);
        string text = Untrace(statusText.Trim());
        return Build(text.Length > 0 ? $"{failure.TrimEnd('.', ' ')}: {text}" : failure, null, hintFor(null), fallbackHint: null);
    }

    private static bool HasReason((LiteRtStatusCode Code, string Message)? report) => report is { Message.Length: > 0 };

    private static LiteRtException Build(
        string failure, (LiteRtStatusCode Code, string Message)? report, string? hint, string? fallbackHint)
    {
        var sb = new StringBuilder(failure.TrimEnd('.', ' '));
        if (report is { } r)
        {
            sb.Append(": ").Append(CanonicalName(r.Code));
            if (r.Message.Length > 0)
                sb.Append(": ").Append(r.Message);
        }
        EndSentence(sb);
        if (report is { Message.Length: 0 })
            sb.Append(" The runtime gave no reason: with the native log silenced (LiteRtEngine.SetMinLogLevel above 5), " +
                      "LiteRT drops it; a level of 5 or lower keeps it.");
        if (!HasReason(report) && fallbackHint is not null)
            sb.Append(' ').Append(fallbackHint);
        if (hint is not null)
        {
            EndSentence(sb);
            sb.Append(' ').Append(hint);
        }
        string message = sb.ToString();
        return report is { } rep ? new LiteRtException(message, rep.Code) : new LiteRtException(message);
    }

    /// <summary>The absl canonical spelling (<c>INVALID_ARGUMENT</c>, …) the native logs use, so a reported
    /// status reads the same in an exception and in the runtime's own stderr.</summary>
    internal static string CanonicalName(LiteRtStatusCode code) => code switch
    {
        LiteRtStatusCode.Ok => "OK",
        LiteRtStatusCode.Cancelled => "CANCELLED",
        LiteRtStatusCode.InvalidArgument => "INVALID_ARGUMENT",
        LiteRtStatusCode.DeadlineExceeded => "DEADLINE_EXCEEDED",
        LiteRtStatusCode.NotFound => "NOT_FOUND",
        LiteRtStatusCode.AlreadyExists => "ALREADY_EXISTS",
        LiteRtStatusCode.PermissionDenied => "PERMISSION_DENIED",
        LiteRtStatusCode.ResourceExhausted => "RESOURCE_EXHAUSTED",
        LiteRtStatusCode.FailedPrecondition => "FAILED_PRECONDITION",
        LiteRtStatusCode.Aborted => "ABORTED",
        LiteRtStatusCode.OutOfRange => "OUT_OF_RANGE",
        LiteRtStatusCode.Unimplemented => "UNIMPLEMENTED",
        LiteRtStatusCode.Internal => "INTERNAL",
        LiteRtStatusCode.Unavailable => "UNAVAILABLE",
        LiteRtStatusCode.DataLoss => "DATA_LOSS",
        LiteRtStatusCode.Unauthenticated => "UNAUTHENTICATED",
        _ => "UNKNOWN",
    };

    private static void EndSentence(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] is not ('.' or '!' or '?'))
            sb.Append('.');
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace LiteRtLmSharp.Native;

/// <summary>
/// Turns a failed LiteRT-LM C API call into a <see cref="LiteRtException"/> that carries the status the call
/// returned and the reason the runtime recorded (<c>c/error_reporter.h</c>), so a failure surfaces the
/// runtime's own words instead of a bare status.
/// </summary>
/// <remarks>
/// Every C API function that can fail returns a <c>LiteRtLmStatusCode</c>, which the declarations type as
/// <see cref="LiteRtStatusCode"/> (the values match). A non-OK status also leaves a message on the calling
/// thread, readable with <c>litert_lm_get_last_error_message</c> until the next failure or clear on that
/// thread; a successful call never touches it. The binding therefore (1) decides failure from the returned
/// status, never from the message; (2) reads the message on the same thread right after the failing call,
/// then clears it, which also frees it on long-lived thread-pool threads; and (3) does not clear the message
/// before a call, because every failure overwrites it and the message is only read after a failure. Every
/// call site is synchronous on one thread: the blocking sends that the async APIs move to the thread pool
/// move there as a whole, read included. A failure the binding maps to a value instead of an exception
/// calls <see cref="Clear"/> so the slot stays empty.
/// </remarks>
internal static class NativeError
{
    /// <summary>Clears the calling thread's message, for a failure the binding maps to a value instead of
    /// reporting it. (TRANSITION: the model-info, embedding and conversation call sites also call it before
    /// a call until they are migrated.)</summary>
    internal static void Clear() => LiteRtLmNative.litert_lm_clear_last_error();

    /// <summary>
    /// Throws a <see cref="LiteRtException"/> when <paramref name="status"/> is not OK. The message is
    /// "<paramref name="function"/> failed", then the status and the runtime's reason, then
    /// <paramref name="hint"/> when given.
    /// </summary>
    internal static void ThrowIfFailed(LiteRtStatusCode status, string function, string? hint = null)
    {
        if (status != LiteRtStatusCode.Ok)
            throw Exception(status, $"{function} failed.", hint);
    }

    /// <summary>
    /// Reads and clears the calling thread's message for a call that returned <paramref name="status"/>
    /// (not OK). The code is the status itself, an unrecognized value read as
    /// <see cref="LiteRtStatusCode.Unknown"/>; the message is the runtime's reason without the leading
    /// code (see <see cref="Detail"/>), empty when it gave none.
    /// </summary>
    internal static (LiteRtStatusCode Code, string Message) Take(LiteRtStatusCode status)
    {
        // The header freezes the canonical set but asks callers to read unrecognized values as Unknown, so a
        // binding built against this version stays correct with a newer native library.
        LiteRtStatusCode code = Enum.IsDefined(status) ? status : LiteRtStatusCode.Unknown;
        return (code, Detail(code, TakeMessage()));
    }

    private static string? TakeMessage()
    {
        string? message = Marshal.PtrToStringUTF8(LiteRtLmNative.litert_lm_get_last_error_message());
        LiteRtLmNative.litert_lm_clear_last_error();
        return message;
    }

    /// <summary>
    /// Builds the exception for a call that returned <paramref name="status"/> (not OK). The message is
    /// <paramref name="failure"/> (what failed), then the status and the runtime's reason;
    /// <paramref name="fallbackHint"/> only when the runtime gave no reason (the binding's guess at the usual
    /// causes); and <paramref name="hint"/> in both cases (guidance that stays useful next to the runtime's
    /// reason).
    /// </summary>
    internal static LiteRtException Exception(
        LiteRtStatusCode status, string failure, string? hint = null, string? fallbackHint = null)
        => Build(failure, Take(status), hint, fallbackHint);

    /// <summary>
    /// Builds the exception like <see cref="Exception(LiteRtStatusCode, string, string?, string?)"/>, choosing
    /// the guidance from the runtime's report, for hints that only fit some causes.
    /// <paramref name="hintFor"/> receives <c>null</c> when the runtime gave no reason.
    /// </summary>
    internal static LiteRtException Exception(
        LiteRtStatusCode status, string failure,
        Func<(LiteRtStatusCode Code, string Message)?, string?> hintFor, string? fallbackHint = null)
    {
        var report = Take(status);
        return Build(failure, report, hintFor(HasReason(report) ? report : null), fallbackHint);
    }

    // TRANSITION: removed when the conversation area is migrated. The call sites of the model-info,
    // embedding and conversation functions still use their pre-status declarations, which return no
    // status; these members report the thread's message with an Unknown status, or no status when the
    // runtime left no message. Their names differ from the status-first members on purpose: a call of
    // Exception that passes a failure text without a status does not compile, so a migrated call site
    // cannot report Unknown instead of the status it received; the LegacyException calls that remain can be
    // found by name, and stop compiling when these members are deleted.

    /// <summary>TRANSITION: removed when the conversation area is migrated. Reads and clears the calling
    /// thread's message for a call that returned no status; <c>null</c> when the runtime left none.</summary>
    internal static (LiteRtStatusCode Code, string Message)? LegacyTake()
    {
        string? message = TakeMessage();
        if (string.IsNullOrEmpty(message))
            return null;
        return (LiteRtStatusCode.Unknown, Detail(LiteRtStatusCode.Unknown, message));
    }

    /// <summary>TRANSITION: removed when the conversation area is migrated. Builds the exception for a call
    /// that returned no status, from the thread's message.</summary>
    internal static LiteRtException LegacyException(string failure, string? hint = null, string? fallbackHint = null)
        => Build(failure, LegacyTake(), hint, fallbackHint);

    /// <summary>TRANSITION: removed when the conversation area is migrated. Builds the exception for a call
    /// that returned no status, choosing the guidance from the thread's message.</summary>
    internal static LiteRtException LegacyException(
        string failure, Func<(LiteRtStatusCode Code, string Message)?, string?> hintFor, string? fallbackHint = null)
    {
        var report = LegacyTake();
        return Build(failure, report, hintFor(HasReason(report) ? report : null), fallbackHint);
    }

    /// <summary>
    /// Reads a status the runtime delivered as text, such as the error string of a stream chunk
    /// (<c>"INVALID_ARGUMENT: reason"</c>): the status from its leading canonical name, and the detail
    /// normalized like <see cref="Take(LiteRtStatusCode)"/>. <c>null</c> when the text starts with no
    /// canonical name.
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
    /// stores either the full absl status text (<c>"INVALID_ARGUMENT: Invalid magic number…"</c>) or a bare
    /// reason (<c>"Unknown LiteRtLmLogSeverity."</c>); a leading code is dropped to avoid printing it twice.
    /// With the native log silenced (a level above 5, such as 1000), LiteRT's status macros record no reason
    /// at all and the text is the bare <c>"INVALID_ARGUMENT: "</c>.
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
    /// Builds the exception for a failure the runtime delivered as status text rather than as a returned
    /// status, such as a stream chunk's error, so it carries the same status, one-line reason and guidance
    /// as a failed blocking call.
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

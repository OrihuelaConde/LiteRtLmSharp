namespace LiteRtLmSharp;

/// <summary>Raised when a LiteRT-LM native call fails.</summary>
/// <remarks>
/// When the native runtime reports why a call failed, the message carries that reason (for example
/// <c>litert_lm_engine_create returned null: INVALID_ARGUMENT: …</c>) and <see cref="StatusCode"/> holds
/// its status. Failures detected by the binding itself, and native failures that report no reason, leave
/// <see cref="StatusCode"/> <c>null</c>.
/// </remarks>
public class LiteRtException : Exception
{
    /// <summary>Creates the exception with a message describing the native failure.</summary>
    public LiteRtException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the underlying exception that caused it.</summary>
    public LiteRtException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates the exception with a message and the status the native runtime reported.</summary>
    public LiteRtException(string message, LiteRtStatusCode statusCode) : base(message) => StatusCode = statusCode;

    /// <summary>
    /// Gets the status the native runtime reported for the failed call, or <c>null</c> when it reported
    /// none or the binding detected the failure itself.
    /// </summary>
    /// <remarks>Use it to branch on the kind of failure, such as <see cref="LiteRtStatusCode.NotFound"/> or
    /// <see cref="LiteRtStatusCode.InvalidArgument"/>, without parsing the message.</remarks>
    public LiteRtStatusCode? StatusCode { get; }
}

/// <summary>
/// The canonical status codes the LiteRT-LM runtime reports for a failed call (the same values as
/// <c>absl::StatusCode</c> and <c>google.rpc.Code</c>). See <see cref="LiteRtException.StatusCode"/>.
/// </summary>
/// <remarks>The values are frozen by the native API; a code this version does not recognize is read as
/// <see cref="Unknown"/>.</remarks>
public enum LiteRtStatusCode
{
    /// <summary>Not an error.</summary>
    Ok = 0,

    /// <summary>The operation was cancelled.</summary>
    Cancelled = 1,

    /// <summary>An unknown error, or a code this version does not recognize.</summary>
    Unknown = 2,

    /// <summary>An argument was invalid (for example a malformed setting or an unusable cache directory).</summary>
    InvalidArgument = 3,

    /// <summary>A deadline expired before the operation completed.</summary>
    DeadlineExceeded = 4,

    /// <summary>A requested entity (such as a file) was not found.</summary>
    NotFound = 5,

    /// <summary>The entity to create already exists.</summary>
    AlreadyExists = 6,

    /// <summary>The caller lacks permission for the operation.</summary>
    PermissionDenied = 7,

    /// <summary>A resource (memory, context space, a quota) is exhausted.</summary>
    ResourceExhausted = 8,

    /// <summary>The system is not in a state the operation requires.</summary>
    FailedPrecondition = 9,

    /// <summary>The operation was aborted.</summary>
    Aborted = 10,

    /// <summary>The operation went past a valid range.</summary>
    OutOfRange = 11,

    /// <summary>The operation is not implemented or not supported (for example by this backend).</summary>
    Unimplemented = 12,

    /// <summary>An internal error in the runtime.</summary>
    Internal = 13,

    /// <summary>The service is currently unavailable.</summary>
    Unavailable = 14,

    /// <summary>Unrecoverable data loss or corruption.</summary>
    DataLoss = 15,

    /// <summary>The request lacks valid authentication.</summary>
    Unauthenticated = 16,
}

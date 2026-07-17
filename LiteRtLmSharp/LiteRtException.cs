namespace LiteRtLmSharp;

/// <summary>Raised when a LiteRT-LM native call fails.</summary>
public class LiteRtException : Exception
{
    /// <summary>Creates the exception with a message describing the native failure.</summary>
    public LiteRtException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the underlying exception that caused it.</summary>
    public LiteRtException(string message, Exception innerException) : base(message, innerException) { }
}

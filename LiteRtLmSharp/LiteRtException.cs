namespace LiteRtLmSharp;

/// <summary>Raised when a LiteRT-LM native call fails.</summary>
public sealed class LiteRtException : Exception
{
    public LiteRtException(string message) : base(message) { }
    public LiteRtException(string message, Exception innerException) : base(message, innerException) { }
}

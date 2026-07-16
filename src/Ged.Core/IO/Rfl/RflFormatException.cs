namespace Ged.Core.IO.Rfl;

/// <summary>Thrown when an RFL/RFG file cannot be parsed as expected.</summary>
public sealed class RflFormatException : Exception
{
    public RflFormatException(string message) : base(message)
    {
    }

    public RflFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}

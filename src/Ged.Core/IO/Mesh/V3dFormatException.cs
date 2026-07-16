namespace Ged.Core.IO.Mesh;

/// <summary>Thrown when V3M/V3C mesh data is malformed or an unsupported version.</summary>
public sealed class V3dFormatException : Exception
{
    public V3dFormatException(string message)
        : base(message)
    {
    }

    public V3dFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

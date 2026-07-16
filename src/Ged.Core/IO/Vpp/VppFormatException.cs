namespace Ged.Core.IO.Vpp;

/// <summary>Thrown when a byte stream does not conform to the VPP v1 packfile format.</summary>
public sealed class VppFormatException : Exception
{
    public VppFormatException(string message)
        : base(message)
    {
    }

    public VppFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

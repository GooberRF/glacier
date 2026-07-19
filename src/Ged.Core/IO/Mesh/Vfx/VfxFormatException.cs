namespace Ged.Core.IO.Mesh.Vfx;

/// <summary>Thrown when a byte buffer is not a structurally-valid VFX (VSFX) effect file.</summary>
public sealed class VfxFormatException : Exception
{
    public VfxFormatException(string message)
        : base(message)
    {
    }

    public VfxFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

namespace Ged.Core.IO.Tex;

/// <summary>Thrown when texture data is malformed or in an unsupported variant.</summary>
public sealed class TextureFormatException : Exception
{
    public TextureFormatException(string message)
        : base(message)
    {
    }

    public TextureFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

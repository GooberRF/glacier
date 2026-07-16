namespace Ged.Core.Model;

/// <summary>
/// An 8-bit-per-channel RGBA color, serialized as four bytes in R, G, B, A
/// order (RFL <c>color</c> type).
/// </summary>
public record struct RfColor(byte R, byte G, byte B, byte A);

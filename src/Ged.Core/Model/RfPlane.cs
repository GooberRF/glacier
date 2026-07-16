namespace Ged.Core.Model;

/// <summary>
/// A 3D plane: a normal vector plus a signed distance from the origin
/// (RFL <c>plane</c> type).
/// </summary>
public record struct RfPlane(Vec3 Normal, float Offset);

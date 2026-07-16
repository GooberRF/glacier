namespace Ged.Core.Model;

/// <summary>Axis-aligned bounding box defined by two corner points (RFL <c>aabb</c>).</summary>
public record struct Aabb(Vec3 P1, Vec3 P2);

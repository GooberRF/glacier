using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Tests.Compiler;

/// <summary>Helpers to build brushes and compile them for the fixture tests.</summary>
public static class CompilerTestBrushes
{
    /// <summary>An air box of the given size centred at <paramref name="center"/>.</summary>
    public static Brush AirBox(int uid, Vec3 center, float w, float h, float d, string texture = "wall")
        => MakeBox(uid, center, w, h, d, BrushFlags.Air, texture);

    /// <summary>A solid box centred at <paramref name="center"/>.</summary>
    public static Brush SolidBox(int uid, Vec3 center, float w, float h, float d, string texture = "wall")
        => MakeBox(uid, center, w, h, d, BrushFlags.None, texture);

    /// <summary>A solid detail box.</summary>
    public static Brush DetailBox(int uid, Vec3 center, float w, float h, float d, int life = -1, string texture = "wall")
    {
        Brush b = MakeBox(uid, center, w, h, d, BrushFlags.Detail, texture);
        b.Life = life;
        return b;
    }

    public static Brush MakeBox(int uid, Vec3 center, float w, float h, float d, BrushFlags flags, string texture)
    {
        Geometry g = BrushFactory.Box(w, h, d, 0, 0, 0, texture);
        return new Brush
        {
            Uid = uid,
            Position = center,
            Rotation = Mat3.Identity,
            Geometry = g,
            Flags = (uint)flags,
            Life = -1,
            State = BrushState.Normal,
        };
    }

    /// <summary>Room-space centre of a compiled room's AABB.</summary>
    public static Vec3 RoomCenter(Room r) => new(
        (r.Aabb.P1.X + r.Aabb.P2.X) * 0.5f,
        (r.Aabb.P1.Y + r.Aabb.P2.Y) * 0.5f,
        (r.Aabb.P1.Z + r.Aabb.P2.Z) * 0.5f);
}

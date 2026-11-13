using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Structure gate for RED's air-portal chop (compiler-parity-notes.md "Air portal brushes and inside-out rooms"):
/// on levels built around air portal brushes, Glacier's default build must give every air portal brush the
/// portal faces Alpine RED's build of the same level gives it — the same total portal-face area inside the brush
/// box and the same number of room pairs joined through it — and the level's main-room and portal counts must
/// equal Alpine RED's. Skips when the Alpine RED references are absent (gitignored fixtures).
/// </summary>
public sealed class AirPortalStructureGateTests
{
    private readonly ITestOutputHelper _out;

    public AirPortalStructureGateTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [Trait("Category", "DeepGate")] // corpus compile; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
    [InlineData("ctf04.rfl")]
    [InlineData("ctf05.rfl")]
    [InlineData("ctf06.rfl")]
    [InlineData("dm06.rfl")]
    [InlineData("dm12.rfl")]
    [InlineData("dm16.rfl")]
    [InlineData("dm20.rfl")]
    public void Every_Air_Portal_Brush_Gets_Alpine_REDs_Portal_Faces(string name)
    {
        if (AlpineReference.Find() is not { } refs || refs.Get(name) is not { } level
            || !AlpineReference.TryLoad(level, out AlpineReference.Loaded? loaded) || loaded is null)
        {
            return;
        }

        Geometry mine = GeometryCompiler.Compile(loaded.Brushes, loaded.Effects, new CompileOptions { BuildSurfaces = false }).Geometry;
        Geometry alpine = loaded.Reference;

        Assert.Equal(AlpineReference.MainRooms(alpine), AlpineReference.MainRooms(mine));
        Assert.Equal(alpine.Portals.Count, mine.Portals.Count);

        int brushes = 0;
        foreach (Brush b in loaded.Brushes)
        {
            const uint AirPortal = (uint)(BrushFlags.Air | BrushFlags.Portal);
            if ((b.Flags & AirPortal) != AirPortal)
            {
                continue;
            }

            brushes++;
            List<CsgFace> faces = BrushWorld.ToWorldFaces(b, 0, out _);
            Vec3 mn = new(float.MaxValue, float.MaxValue, float.MaxValue), mx = new(float.MinValue, float.MinValue, float.MinValue);
            foreach (CsgFace f in faces)
            {
                f.GrowAabb(ref mn, ref mx);
            }

            (float areaMine, int pairsMine) = PortalsInBox(mine, mn, mx);
            (float areaAlpine, int pairsAlpine) = PortalsInBox(alpine, mn, mx);
            Assert.True(MathF.Abs(areaMine - areaAlpine) <= 0.01f * MathF.Max(1f, areaAlpine),
                $"{name} portal brush {b.Uid}: portal-face area {areaMine:0.###} vs Alpine RED {areaAlpine:0.###}");
            Assert.True(pairsMine == pairsAlpine,
                $"{name} portal brush {b.Uid}: joins {pairsMine} room pair(s) vs Alpine RED {pairsAlpine}");
        }

        _out.WriteLine($"{name}: {brushes} air portal brushes match Alpine RED; main rooms {AlpineReference.MainRooms(mine)}, portals {mine.Portals.Count}");
        Assert.True(brushes > 0);
    }

    /// <summary>Total area of the portal faces lying inside the box, and the distinct room pairs their portals join.</summary>
    private static (float Area, int Pairs) PortalsInBox(Geometry g, Vec3 mn, Vec3 mx)
    {
        const float Eps = 0.02f;
        float area = 0f;
        var pairs = new HashSet<(int, int)>();
        foreach (Face f in g.Faces)
        {
            if (f.PortalIndexPlus2 < 2 || f.PortalIndexPlus2 - 2 >= g.Portals.Count)
            {
                continue;
            }

            bool inBox = true;
            foreach (FaceVertex fv in f.Vertices)
            {
                Vec3 p = g.Vertices[fv.Index];
                inBox &= p.X >= mn.X - Eps && p.X <= mx.X + Eps && p.Y >= mn.Y - Eps && p.Y <= mx.Y + Eps && p.Z >= mn.Z - Eps && p.Z <= mx.Z + Eps;
            }

            if (!inBox)
            {
                continue;
            }

            Vec3 sum = default;
            Vec3 a = g.Vertices[f.Vertices[0].Index];
            for (int i = 1; i + 1 < f.Vertices.Count; i++)
            {
                sum = sum.Add(g.Vertices[f.Vertices[i].Index].Sub(a).Cross(g.Vertices[f.Vertices[i + 1].Index].Sub(a)));
            }

            area += sum.Length() * 0.5f;
            Portal portal = g.Portals[f.PortalIndexPlus2 - 2];
            pairs.Add((Math.Min(portal.RoomIndex1, portal.RoomIndex2), Math.Max(portal.RoomIndex1, portal.RoomIndex2)));
        }

        return (area, pairs.Count);
    }
}

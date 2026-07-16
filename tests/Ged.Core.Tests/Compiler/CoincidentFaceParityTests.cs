using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// RED-parity coverage for coincident/coplanar face resolution — the flagship
/// item-1 divergence. When an AIR brush and a SOLID brush contribute exactly
/// coplanar, overlapping faces (the "air panel, then a solid at the same place"
/// idiom that is pervasive in community levels), RED attributes the wall to the
/// SOLID brush (its texture, its face) and drops the air brush's coincident
/// fragment — regardless of brush time order. GED previously emitted BOTH,
/// so the air brush's texture (e.g. Rck_Default) z-fought over the intended solid
/// surface. These tests lock RED's semantics against the exact user-reported
/// cases (dmabruptdecay brushes 85/92, dm04 brushes 11/14) plus the canonical
/// synthetic.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class CoincidentFaceParityTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    private static Brush AirBox(int uid, Vec3 c, float w, float h, float d, string tex) =>
        CompilerTestBrushes.MakeBox(uid, c, w, h, d, BrushFlags.Air, tex);

    private static Brush SolidBox(int uid, Vec3 c, float w, float h, float d, string tex) =>
        CompilerTestBrushes.MakeBox(uid, c, w, h, d, BrushFlags.None, tex);

    // --- Canonical synthetic: air box then an identical solid box, later in time ---

    [Fact]
    public void Coincident_Air_Then_Solid_Yields_The_Solids_Faces_Not_The_Airs()
    {
        // A big air room supplies the open space; an air "panel" and an identical
        // solid box share the [-3,3]^3 cube (solid added LATER). Every surviving
        // face on the cube boundary must carry the solid's texture — none the air's.
        var brushes = new List<Brush>
        {
            AirBox(1, V(0, 0, 0), 20, 20, 20, "roomtex"),
            AirBox(2, V(0, 0, 0), 6, 6, 6, "airtex"),
            SolidBox(3, V(0, 0, 0), 6, 6, 6, "solidtex"),
        };

        Geometry g = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = false }).Geometry;

        int cubeFaces = 0, airOnCube = 0;
        foreach (Face f in g.Faces)
        {
            if (!OnCubeBoundary(g, f, 3f))
            {
                continue;
            }

            cubeFaces++;
            string tex = TexName(g, f);
            Assert.Equal("solidtex", tex);
            if (tex == "airtex")
            {
                airOnCube++;
            }
        }

        Assert.True(cubeFaces >= 6, $"expected the solid cube's 6 walls, found {cubeFaces}");
        Assert.Equal(0, airOnCube);
        Assert.DoesNotContain(g.Faces, f => TexName(g, f) == "airtex");
    }

    /// <summary>True when every vertex of the face sits on the surface of the ±half cube.</summary>
    private static bool OnCubeBoundary(Geometry g, Face f, float half)
    {
        if (f.Vertices.Count < 3)
        {
            return false;
        }

        bool allOnAxisPlane = true;
        foreach (FaceVertex fv in f.Vertices)
        {
            Vec3 p = g.Vertices[fv.Index];
            bool onFace =
                (MathF.Abs(MathF.Abs(p.X) - half) < 0.01f && MathF.Abs(p.Y) <= half + 0.01f && MathF.Abs(p.Z) <= half + 0.01f) ||
                (MathF.Abs(MathF.Abs(p.Y) - half) < 0.01f && MathF.Abs(p.X) <= half + 0.01f && MathF.Abs(p.Z) <= half + 0.01f) ||
                (MathF.Abs(MathF.Abs(p.Z) - half) < 0.01f && MathF.Abs(p.X) <= half + 0.01f && MathF.Abs(p.Y) <= half + 0.01f);
            if (!onFace)
            {
                allOnAxisPlane = false;
                break;
            }
        }

        return allOnAxisPlane;
    }

    // --- Real levels: assert GED's attribution matches RED's ORIGINAL geometry ---

    [Fact]
    public void Dmabrupt_Solid92_Replaces_Air85_On_The_Coincident_Wall()
    {
        if (!TryLoad("dmabruptdecayrc2a27.rfl", out Geometry orig, out var brushes, out var effects))
        {
            return;
        }

        // Brush 92 (solid) is coincident with part of air panel 85 on the z=-14 and
        // z=-15 planes. RED shows the solid's textures there and NO Rck_Default; GED
        // used to leak air-85's Rck_Default fragments. Probe brush 92's footprint.
        Vec3 min = V(10.5f, -2f, -15f), max = V(12.5f, 4f, -14f);
        Geometry mine = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false }).Geometry;

        // RED ground truth: no Rck_Default in this footprint.
        Assert.DoesNotContain(FacesInBox(orig, min, max), t => string.Equals(t, "Rck_Default.tga", StringComparison.OrdinalIgnoreCase));

        // GED must match: the air panel's Rck_Default is fully replaced by the solid.
        Assert.DoesNotContain(FacesInBox(mine, min, max), t => string.Equals(t, "Rck_Default.tga", StringComparison.OrdinalIgnoreCase));

        // ...and the solid's own wall textures are present (face presence).
        List<string> mineTex = FacesInBox(mine, min, max).ToList();
        Assert.Contains(mineTex, t => t.StartsWith("mtl_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dm04_Solid11_Owns_The_Shared_Floor_Not_Air14()
    {
        if (!TryLoad("dm04.rfl", out Geometry orig, out var brushes, out var effects))
        {
            return;
        }

        // Solid brush 11 (top at y=-60.2) meets air brush 14 (bottom at y=-60.2).
        // RED floors this with 11's rck_comflrtrax03 only; GED used to also emit an
        // air-14 rck_canyon_rock02 face "cutting out of" 11. Probe the shared floor
        // plane in the 11/14 overlap footprint.
        Vec3 min = V(18.9f, -60.7f, -6.5f), max = V(33.3f, -59.7f, 34.3f);
        Geometry mine = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false }).Geometry;

        float redCanyon = FloorCanyonArea(orig, min, max);
        float mineCanyon = FloorCanyonArea(mine, min, max);

        // RED emits essentially no canyon-rock on this floor plane; GED must not
        // over-emit it (it used to add ~46 units of spurious air-14 floor).
        Assert.True(redCanyon < 5f, $"RED baseline unexpectedly has {redCanyon:F1} canyon floor");
        Assert.True(mineCanyon <= redCanyon + 5f,
            $"GED emits {mineCanyon:F1} canyon-rock on the shared floor vs RED's {redCanyon:F1} (spurious air-14 face)");
    }

    [Fact]
    public void Ctf02_Shared_Wall_Present_And_Overall_Area_Holds()
    {
        if (!TryLoad("ctf02.rfl", out Geometry orig, out var brushes, out var effects))
        {
            return;
        }

        // Air brushes 10513 and 8 meet at x=12. That wall is fragmented differently
        // by GED's point-sample survival (a documented, separate over-split residual
        // the coincident-face fix does not target). What the fix DOES guarantee here
        // is no regression: the wall still exists (rck_redsoyna01 present at x=12) and
        // the level's overall textured-area parity with RED holds.
        Geometry mine = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false }).Geometry;

        // Face presence: the shared wall at x=12 is not culled away.
        Vec3 wallMin = V(11.9f, -24f, -28.3f), wallMax = V(12.1f, -19.8f, -7f);
        float wall = 0;
        foreach (Face f in mine.Faces)
        {
            if (f.Vertices.Count >= 3 && f.Texture >= 0 && MathF.Abs(f.Plane.Normal.X) > 0.99f &&
                InBox(Centroid(mine, f), wallMin, wallMax) &&
                TexName(mine, f).StartsWith("rck_redsoyna01", StringComparison.OrdinalIgnoreCase))
            {
                wall += Area(mine, f);
            }
        }

        Assert.True(wall > 2f, $"the 10513/8 shared wall at x=12 is missing (area {wall:F1})");

        // Overall attribution: total textured area stays within tolerance of RED
        // (the coincident-air-vs-solid fix nudges ctf02 CLOSER to RED, not away).
        float red = TotalTexArea(orig), mineTot = TotalTexArea(mine);
        float diff = MathF.Abs(mineTot - red) / red;
        Assert.True(diff <= 0.05f, $"ctf02 total textured area diverges {diff * 100:F1}% (RED={red:F0} GED={mineTot:F0})");
    }

    [Fact]
    public void Dmabrupt_Air86_And_Air108_Share_One_Wall_Not_Two()
    {
        if (!TryLoad("dmabruptdecayrc2a27.rfl", out Geometry orig, out var brushes, out var effects))
        {
            return;
        }

        // Air brushes 86 (doc/time index 30, earlier) and 108 (index 38, later) are two OVERLAPPING
        // air rooms sharing the wall plane x=25.5 (both carry a coincident -X face there). AIR-vs-AIR,
        // aligned normals => class 3, mode 3. The survival table DAT_0057cc48 was RE-VERIFIED in RED.exe
        // (flagship 17, ghidraRF): consumer FUN_004a7480 keeps a face iff table[class+(mode+operand*8)*5]
        // != 2; world/earlier(86)=table[18]=2 => DROP, brush/later(108)=table[58]=1 => KEEP. So in the
        // COINCIDENT overlap the LATER air brush wins, which is exactly what GED does; GED used to emit
        // BOTH (z-fight) and now keeps one, matching RED's area (this test).
        //
        // Goober's "GED shows 108, RED shows 86" is the NON-overlap remainder: RED keeps 86's
        // mtl_filthypanels01 panel where it does NOT overlap 108 (a distinct z-band) plus 108's
        // filthypanels02 where it does; GED loses 86's exclusive panel to a partial-overlap dissolve in the
        // incremental fold (same geometry/area, wrong texture on ~2.5 m2). Documented non-blocking gap
        // (compiler-parity-notes.md) — NOT a survival-table tie-break error; the binary confirms GED's
        // coincident decision is correct.
        Geometry mine = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false }).Geometry;

        float red = WallArea(orig, 25.5f), ged = WallArea(mine, 25.5f);
        Assert.True(red > 50f, $"RED baseline missing the 86/108 shared wall (area {red:F1})");
        Assert.True(ged > red * 0.6f, $"the 86/108 shared wall at x=25.5 is missing (GED {ged:F1} vs RED {red:F1})");
        Assert.True(ged <= red * 1.25f,
            $"the 86/108 shared wall is doubled: GED {ged:F1} vs RED {red:F1} (air-vs-air coincidence not resolved)");
    }

    /// <summary>Textured area of near-±X faces sitting on the plane x≈<paramref name="x"/>.</summary>
    private static float WallArea(Geometry g, float x)
    {
        float sum = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count >= 3 && f.Texture >= 0 && MathF.Abs(f.Plane.Normal.X) > 0.99f &&
                MathF.Abs(Centroid(g, f).X - x) < 0.2f)
            {
                sum += Area(g, f);
            }
        }

        return sum;
    }

    private static float TotalTexArea(Geometry g)
    {
        float sum = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count >= 3 && f.Texture >= 0)
            {
                sum += Area(g, f);
            }
        }

        return sum;
    }

    // --- helpers ---

    private static bool TryLoad(string file, out Geometry orig, out List<Brush> brushes, out List<RoomEffect> effects)
    {
        orig = null!;
        brushes = new List<Brush>();
        effects = new List<RoomEffect>();
        if (!Corpus.Available)
        {
            return false;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return false;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? o = null;
        BrushesSection? b = null;
        RoomEffectsSection? e = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                o ??= gs.Geometry;
            }
            else if (s.Content is BrushesSection bs)
            {
                b ??= bs;
            }
            else if (s.Content is RoomEffectsSection es)
            {
                e ??= es;
            }
        }

        if (o is null || b is null)
        {
            return false;
        }

        orig = o;
        brushes = b.Brushes.ToList();
        effects = e?.Effects.ToList() ?? new List<RoomEffect>();
        return true;
    }

    private static string TexName(Geometry g, Face f) =>
        f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : string.Empty;

    private static Vec3 Centroid(Geometry g, Face f)
    {
        var c = new Vec3(0, 0, 0);
        foreach (FaceVertex v in f.Vertices)
        {
            c = c.Add(g.Vertices[v.Index]);
        }

        return f.Vertices.Count == 0 ? c : c.Scale(1f / f.Vertices.Count);
    }

    private static float Area(Geometry g, Face f)
    {
        if (f.Vertices.Count < 3)
        {
            return 0;
        }

        Vec3 c = Centroid(g, f);
        float area = 0;
        for (int i = 0; i < f.Vertices.Count; i++)
        {
            Vec3 a = g.Vertices[f.Vertices[i].Index].Sub(c);
            Vec3 b = g.Vertices[f.Vertices[(i + 1) % f.Vertices.Count].Index].Sub(c);
            area += a.Cross(b).Length() * 0.5f;
        }

        return area;
    }

    private static bool InBox(Vec3 p, Vec3 min, Vec3 max) =>
        p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;

    private static IEnumerable<string> FacesInBox(Geometry g, Vec3 min, Vec3 max)
    {
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count >= 3 && InBox(Centroid(g, f), min, max) && f.Texture >= 0)
            {
                yield return TexName(g, f);
            }
        }
    }

    /// <summary>Canyon-rock area on the up-facing floor plane (n≈+Y) within the box.</summary>
    private static float FloorCanyonArea(Geometry g, Vec3 min, Vec3 max)
    {
        float sum = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3 || f.Texture < 0)
            {
                continue;
            }

            if (f.Plane.Normal.Y > 0.99f && InBox(Centroid(g, f), min, max) &&
                TexName(g, f).StartsWith("rck_canyon", StringComparison.OrdinalIgnoreCase))
            {
                sum += Area(g, f);
            }
        }

        return sum;
    }
}

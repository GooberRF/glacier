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
/// Flagship 24 — LIQUID SURFACE coverage gate. Goober's in-game report: the dmabrupt water surface has a
/// "gaping hole" in it. Root cause: the flagship-22 coplanar liquid merge (<see cref="CoplanarMerger"/>)
/// accepted non-convex / SELF-INTERSECTING unions (its convexity test used an absolute epsilon on an
/// un-normalised cross product, so genuine reflex angles at the dense T-junction vertices of the surface
/// slipped through and accumulated into spiral polygons). Those faces render as garbage — a visible hole —
/// and their fan-triangulation area far exceeds their true (shoelace) area (dmabrupt inflated 424 → 528 m²).
/// RED emits a clean CONVEX DECOMPOSITION of the open cross-section (every face simple + convex). The gate
/// asserts GED does the same: no self-intersecting liquid surface face on any corpus liquid level, and on
/// dmabrupt the surface covers RED's water cross-section with no large interior gap.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class LiquidSurfaceGateTests
{
    [Fact]
    public void No_Self_Intersecting_Liquid_Surface_Faces()
    {
        if (!Corpus.Available)
        {
            return;
        }

        foreach (string path in Corpus.RflFiles)
        {
            string name = Path.GetFileName(path);
            if (name.Contains(".autosave", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ged", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Load(path, out _, out List<Brush> brushes, out List<RoomEffect> effects, out bool alpine))
            {
                continue;
            }

            Geometry ged;
            try
            {
                ged = GeometryCompiler.Compile(brushes, effects,
                    new CompileOptions { Alpine = alpine, BuildSurfaces = false }).Geometry;
            }
            catch
            {
                continue;
            }

            foreach (Face f in ged.Faces)
            {
                if (((FaceFlags)f.Flags & FaceFlags.LiquidSurface) == 0 || f.Vertices.Count < 3)
                {
                    continue;
                }

                float fan = FanArea(ged, f);
                float shoe = ShoelaceArea(ged, f);
                // A simple planar polygon has fan-area == shoelace-area; a self-intersecting one has
                // fan > shoelace (its overlapping triangles double-count). Allow 2% for float noise.
                Assert.True(fan <= (shoe * 1.02f) + 0.05f,
                    $"{name}: self-intersecting liquid surface face — fanArea {fan:F2} > shoelace {shoe:F2} ({f.Vertices.Count} verts)");
            }
        }
    }

    [Fact]
    public void Dmabrupt_Water_Surface_Covers_Red_Cross_Section()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dmabruptdecayrc2a27.rfl");
        if (!File.Exists(path) || !Load(path, out Geometry red, out List<Brush> brushes, out List<RoomEffect> effects, out bool alpine))
        {
            return;
        }

        Geometry ged = GeometryCompiler.Compile(brushes, effects,
            new CompileOptions { Alpine = alpine, BuildSurfaces = false }).Geometry;

        var redUp = UpFaces(red);
        var gedUp = UpFaces(ged);

        float redArea = redUp.Sum(f => ShoelaceArea(red, f));
        float gedArea = gedUp.Sum(f => ShoelaceArea(ged, f));

        // GED's surface area must match RED's water cross-section (was inflated 211 -> 263 by the
        // self-intersecting merge; now ~212 == RED 211).
        Assert.True(gedArea >= redArea * 0.95f && gedArea <= redArea * 1.10f,
            $"dmabrupt: liquid surface area {gedArea:F1} m² outside 95–110% of RED {redArea:F1} m²");

        // No large CONTIGUOUS interior gap in GED's surface vs RED's coverage (0.25 m grid flood).
        int largest = LargestHole(red, redUp, ged, gedUp, out float cellArea);
        Assert.True(largest * cellArea < 3.0f,
            $"dmabrupt: largest contiguous water-surface hole {largest * cellArea:F1} m² (>= 3.0) — a visible gap");
    }

    // ---- helpers -------------------------------------------------------------------------

    private static List<Face> UpFaces(Geometry g)
    {
        var list = new List<Face>();
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            bool flagged = ((FaceFlags)f.Flags & FaceFlags.LiquidSurface) != 0;
            bool wtr = f.Texture >= 0 && f.Texture < g.Textures.Count &&
                       g.Textures[f.Texture].StartsWith("wtr_", StringComparison.OrdinalIgnoreCase);
            if (!flagged && !wtr)
            {
                continue;
            }

            float ny = 0;
            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                Vec3 a = g.Vertices[f.Vertices[i].Index];
                Vec3 b = g.Vertices[f.Vertices[(i + 1) % n].Index];
                ny += (a.Z - b.Z) * (a.X + b.X);
            }

            if (ny > 0.5f)
            {
                list.Add(f);
            }
        }

        return list;
    }

    private static int LargestHole(Geometry red, List<Face> redUp, Geometry ged, List<Face> gedUp, out float cellArea)
    {
        const float Cell = 0.25f;
        cellArea = Cell * Cell;
        float xmin = float.MaxValue, xmax = float.MinValue, zmin = float.MaxValue, zmax = float.MinValue;
        foreach ((Geometry g, List<Face> up) in new[] { (red, redUp), (ged, gedUp) })
        {
            foreach (Face f in up)
            {
                foreach (FaceVertex v in f.Vertices)
                {
                    Vec3 p = g.Vertices[v.Index];
                    xmin = Math.Min(xmin, p.X);
                    xmax = Math.Max(xmax, p.X);
                    zmin = Math.Min(zmin, p.Z);
                    zmax = Math.Max(zmax, p.Z);
                }
            }
        }

        int nx = (int)((xmax - xmin) / Cell) + 1;
        int nz = (int)((zmax - zmin) / Cell) + 1;
        var hole = new bool[nx, nz];
        for (int ix = 0; ix < nx; ix++)
        {
            for (int iz = 0; iz < nz; iz++)
            {
                float px = xmin + ((ix + 0.5f) * Cell);
                float pz = zmin + ((iz + 0.5f) * Cell);
                if (Covered(red, redUp, px, pz) && !Covered(ged, gedUp, px, pz))
                {
                    hole[ix, iz] = true;
                }
            }
        }

        var seen = new bool[nx, nz];
        int best = 0;
        var stack = new Stack<(int, int)>();
        for (int ix = 0; ix < nx; ix++)
        {
            for (int iz = 0; iz < nz; iz++)
            {
                if (!hole[ix, iz] || seen[ix, iz])
                {
                    continue;
                }

                int size = 0;
                stack.Push((ix, iz));
                seen[ix, iz] = true;
                while (stack.Count > 0)
                {
                    (int cx, int cz) = stack.Pop();
                    size++;
                    foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int a = cx + dx, b = cz + dz;
                        if (a >= 0 && a < nx && b >= 0 && b < nz && hole[a, b] && !seen[a, b])
                        {
                            seen[a, b] = true;
                            stack.Push((a, b));
                        }
                    }
                }

                best = Math.Max(best, size);
            }
        }

        return best;
    }

    private static bool Covered(Geometry g, List<Face> faces, float px, float pz)
    {
        foreach (Face f in faces)
        {
            bool inside = false;
            int n = f.Vertices.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vec3 vi = g.Vertices[f.Vertices[i].Index];
                Vec3 vj = g.Vertices[f.Vertices[j].Index];
                if (((vi.Z > pz) != (vj.Z > pz)) && (px < ((vj.X - vi.X) * (pz - vi.Z) / (vj.Z - vi.Z)) + vi.X))
                {
                    inside = !inside;
                }
            }

            if (inside)
            {
                return true;
            }
        }

        return false;
    }

    private static float FanArea(Geometry g, Face f)
    {
        var c = new Vec3(0, 0, 0);
        foreach (FaceVertex v in f.Vertices)
        {
            c = c.Add(g.Vertices[v.Index]);
        }

        c = c.Scale(1f / f.Vertices.Count);
        float area = 0;
        int n = f.Vertices.Count;
        for (int i = 0; i < n; i++)
        {
            Vec3 a = g.Vertices[f.Vertices[i].Index].Sub(c);
            Vec3 b = g.Vertices[f.Vertices[(i + 1) % n].Index].Sub(c);
            area += a.Cross(b).Length() * 0.5f;
        }

        return area;
    }

    /// <summary>Absolute shoelace area projected onto the face's dominant plane (true simple-polygon area).</summary>
    private static float ShoelaceArea(Geometry g, Face f)
    {
        // Dominant axis of the (approx) normal.
        Vec3 nrm = new(0, 0, 0);
        int n = f.Vertices.Count;
        for (int i = 0; i < n; i++)
        {
            Vec3 a = g.Vertices[f.Vertices[i].Index];
            Vec3 b = g.Vertices[f.Vertices[(i + 1) % n].Index];
            nrm = nrm.Add(new Vec3(
                (a.Y - b.Y) * (a.Z + b.Z),
                (a.Z - b.Z) * (a.X + b.X),
                (a.X - b.X) * (a.Y + b.Y)));
        }

        float ax = Math.Abs(nrm.X), ay = Math.Abs(nrm.Y), az = Math.Abs(nrm.Z);
        int drop = ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);
        float nLen = MathF.Sqrt((nrm.X * nrm.X) + (nrm.Y * nrm.Y) + (nrm.Z * nrm.Z));
        float dom = drop == 0 ? ax : (drop == 1 ? ay : az);
        if (nLen < 1e-9f || dom < 1e-9f)
        {
            return 0;
        }

        double a2 = 0;
        for (int i = 0; i < n; i++)
        {
            Vec3 p = g.Vertices[f.Vertices[i].Index];
            Vec3 q = g.Vertices[f.Vertices[(i + 1) % n].Index];
            (float pu, float pv) = Proj(p, drop);
            (float qu, float qv) = Proj(q, drop);
            a2 += (pu * qv) - (qu * pv);
        }

        // The projected area scales by |n_dominant| / |n|; divide back out for the true 3D area.
        return (float)(Math.Abs(a2) * 0.5 * (nLen / dom));
    }

    private static (float, float) Proj(Vec3 p, int drop) => drop switch
    {
        0 => (p.Y, p.Z),
        1 => (p.Z, p.X),
        _ => (p.X, p.Y),
    };

    private static bool Load(string path, out Geometry geo, out List<Brush> brushes, out List<RoomEffect> effects, out bool alpine)
    {
        geo = null!;
        brushes = new List<Brush>();
        effects = new List<RoomEffect>();
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        alpine = rfl.Context.IsAlpine;
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

        geo = o;
        brushes = MoverBrushes.ExcludeMovers(b.Brushes, MoverBrushes.CollectMoverUids(rfl));
        effects = e?.Effects.ToList() ?? new List<RoomEffect>();
        return true;
    }
}

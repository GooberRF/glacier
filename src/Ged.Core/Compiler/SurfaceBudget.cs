using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// The level's lightmap surface budget: how many surfaces it carries against Alpine RED's cap of
/// <see cref="SurfaceBuilder.MaxLevelSurfaces"/>, and how many lit faces were left without one (surface −1: no lightmap,
/// drawn unshaded at full brightness). Past the cap Alpine RED stops starting surfaces
/// (editor_patch/terrain_build.h:36, terrain_build.cpp:710-717), and so does Glacier; the build, the save, the linter and
/// level.stats all report it from here, so a level of dense organic rock cannot lose its lighting silently.
/// </summary>
public sealed record SurfaceBudgetState(int Faces, int Surfaces, int Wanted, int UnlitFaces, int UnlitBrushes, Vec3? UnlitMin, Vec3? UnlitMax, Vec3? Location, IReadOnlyList<(int Uid, int Faces)> TopBrushes, int EnclosedFaces = 0, int Cap = SurfaceBuilder.MaxLevelSurfaces)
{
    /// <summary>True when lit faces went without a surface.</summary>
    public bool Over => UnlitFaces > 0;

    /// <summary>The build / save / lint warning, or null when every lit face has a surface.</summary>
    public string? Message()
    {
        if (!Over)
        {
            return null;
        }

        string wanted = Wanted > Surfaces ? string.Create(CultureInfo.InvariantCulture, $"needs {Wanted:N0} lightmap surfaces") : "is at the lightmap surface cap";
        string where = UnlitMin is { } a && UnlitMax is { } b
            ? string.Create(CultureInfo.InvariantCulture, $" between ({a.X:0}, {a.Y:0}, {a.Z:0}) and ({b.X:0}, {b.Y:0}, {b.Z:0})")
            : string.Empty;
        string top = TopBrushes.Count > 0
            ? "; most from brush " + string.Join(", ", TopBrushes.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Uid} ({t.Faces})")))
            : string.Empty;
        string brushes = UnlitBrushes > 0 ? string.Create(CultureInfo.InvariantCulture, $" of {UnlitBrushes:N0} brushes") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture,
            $"Lightmap surface cap: the level {wanted}, but a level holds at most {Cap:N0} (Alpine RED's limit). {UnlitFaces:N0} lit faces{brushes} got no surface and draw unlit (full bright, no shadows){where}{top}. Cut the faces players see: coarser organic rock (the kit's organic_budget) or fewer detail pieces.");
    }

    /// <summary>Measures a build's surfaces from the surface builder's result (exact).</summary>
    public static SurfaceBudgetState FromBuild(int faces, SurfaceBuildResult r, int cap = SurfaceBuilder.MaxLevelSurfaces)
    {
        ArgumentNullException.ThrowIfNull(r);
        return Of(faces, r.Surfaces.Count, Math.Max(r.SurfacesWanted, r.Surfaces.Count), r.Unlit.Select(f => (f.SourceBrushUid, Centre(f.Vertices.Select(v => v.Position)))))
            with { EnclosedFaces = r.Enclosed.Count, Cap = cap };
    }

    /// <summary>
    /// Measures compiled geometry as saved: at the cap, every lit-looking face (textured, not a portal, not sky,
    /// invisible, liquid or full-bright) with surface −1 went without a lightmap. Below the cap nothing is reported.
    /// </summary>
    public static SurfaceBudgetState FromGeometry(Geometry? g)
    {
        if (g is null)
        {
            return Of(0, 0, 0, Array.Empty<(int, Vec3)>());
        }

        var unlit = new List<(int, Vec3)>();
        if (g.Surfaces.Count >= SurfaceBuilder.MaxLevelSurfaces)
        {
            const FaceFlags NoSurface = FaceFlags.ShowSky | FaceFlags.IsInvisible | FaceFlags.LiquidSurface | FaceFlags.FullBright;
            foreach (Face f in g.Faces)
            {
                if (f.SurfaceIndex >= 0 || f.IsPortalFace || f.Vertices.Count < 3 || ((FaceFlags)f.Flags & NoSurface) != 0)
                {
                    continue;
                }

                unlit.Add((0, Centre(f.Vertices.Select(v => v.Index >= 0 && v.Index < g.Vertices.Count ? g.Vertices[v.Index] : default))));
            }
        }

        return Of(g.Faces.Count, g.Surfaces.Count, g.Surfaces.Count + unlit.Count, unlit);
    }

    private static SurfaceBudgetState Of(int faces, int surfaces, int wanted, IEnumerable<(int Uid, Vec3 At)> unlit)
    {
        Vec3? min = null, max = null, at = null;
        int n = 0;
        var byBrush = new Dictionary<int, int>();
        foreach ((int uid, Vec3 p) in unlit)
        {
            n++;
            at ??= p;
            min = min is { } a ? Vec3Math.Min(a, p) : p;
            max = max is { } b ? Vec3Math.Max(b, p) : p;
            if (uid != 0)
            {
                byBrush[uid] = byBrush.GetValueOrDefault(uid) + 1;
            }
        }

        var top = byBrush.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(3).Select(kv => (kv.Key, kv.Value)).ToList();
        return new SurfaceBudgetState(faces, surfaces, wanted, n, byBrush.Count, min, max, at, top);
    }

    private static Vec3 Centre(IEnumerable<Vec3> points)
    {
        float x = 0, y = 0, z = 0;
        int n = 0;
        foreach (Vec3 p in points)
        {
            x += p.X;
            y += p.Y;
            z += p.Z;
            n++;
        }

        return n == 0 ? default : new Vec3(x / n, y / n, z / n);
    }
}

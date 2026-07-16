using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// DEFECT 2 diagnostic — dumps RED's baked MOVER surfaces on dmabrupt (the elevators 94/265 and door 10179)
/// and their lightmap luminance, plus how many lightmap pages RED's atlas has vs what GED's static build
/// produces. Establishes whether mover brushes carry their own surfaces into the shared lightmap atlas
/// (so GED regenerating the atlas but leaving the movers section untouched leaves them referencing stale
/// pages → the reported "too dark"). Pure diagnostic (prints only).
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class MoverBakeDiag
{
    private const string Level = "dmabruptdecayrc2a27.rfl";
    private readonly ITestOutputHelper _out;

    public MoverBakeDiag(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump_Red_Mover_Surface_Luminance()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, Level);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();

        var movers = rfl.Sections.Select(s => s.Content).OfType<MoversSection>().FirstOrDefault();
        var lightmaps = rfl.Sections.Select(s => s.Content).OfType<LightmapsSection>().FirstOrDefault();
        Geometry redStatic = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;

        _out.WriteLine($"RED static: rooms={redStatic.Rooms.Count} faces={redStatic.Faces.Count} " +
            $"surfaces={redStatic.Surfaces.Count} lightmapPages={lightmaps?.Lightmaps.Count ?? -1}");

        // Static-surface luminance baseline (what a correctly baked wall looks like).
        if (lightmaps is not null)
        {
            double staticLum = redStatic.Surfaces.Count == 0
                ? -1
                : redStatic.Surfaces.Average(s => SurfaceLuminance(s, lightmaps.Lightmaps));
            _out.WriteLine($"RED static-surface mean luminance = {staticLum:F1} (of 255)");
        }

        if (movers is null)
        {
            _out.WriteLine("no movers section");
            return;
        }

        _out.WriteLine($"movers: {movers.Movers.Count} brushes");
        foreach (Brush m in movers.Movers)
        {
            Geometry mg = m.Geometry;
            int surfCount = mg.Surfaces.Count;
            int litFaces = mg.Faces.Count(f => (f.SurfaceIndex & 0xFFFF) != 0xFFFF && !f.IsPortalFace);
            string pages = string.Join(",", mg.Surfaces.Select(s => s.LightmapIndex).Distinct().OrderBy(x => x));
            double lum = lightmaps is not null && surfCount > 0
                ? mg.Surfaces.Average(s => SurfaceLuminance(s, lightmaps.Lightmaps))
                : -1;
            _out.WriteLine(
                $"  mover uid={m.Uid} pos=({m.Position.X:F1},{m.Position.Y:F1},{m.Position.Z:F1}) " +
                $"faces={mg.Faces.Count} litFaces={litFaces} surfaces={surfCount} pages=[{pages}] meanLum={lum:F1}");
        }

        // Coordinate-space probe: is the mover geometry (and its surfaces) local or world?
        Brush probe = movers.Movers.First(b => b.Uid == 265);
        Geometry pg = probe.Geometry;
        var vmin = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var vmax = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (Vec3 v in pg.Vertices)
        {
            vmin = Vec3Math.Min(vmin, v);
            vmax = Vec3Math.Max(vmax, v);
        }

        _out.WriteLine($"mover 265 pos=({probe.Position.X:F2},{probe.Position.Y:F2},{probe.Position.Z:F2}) " +
            $"geomVertAABB=({vmin.X:F2},{vmin.Y:F2},{vmin.Z:F2})..({vmax.X:F2},{vmax.Y:F2},{vmax.Z:F2})");
        if (pg.Faces.Count > 0)
        {
            RfPlane fp = pg.Faces[0].Plane;
            _out.WriteLine($"  face0 plane n=({fp.Normal.X:F3},{fp.Normal.Y:F3},{fp.Normal.Z:F3}) d={fp.Offset:F3}");
        }

        if (pg.Surfaces.Count > 0)
        {
            Surface s0 = pg.Surfaces[0];
            _out.WriteLine($"  surf0 plane n=({s0.Plane.Normal.X:F3},{s0.Plane.Normal.Y:F3},{s0.Plane.Normal.Z:F3}) " +
                $"d={s0.Plane.Offset:F3} bboxCtr=({(s0.BoundingBox.P1.X + s0.BoundingBox.P2.X) / 2:F2}," +
                $"{(s0.BoundingBox.P1.Y + s0.BoundingBox.P2.Y) / 2:F2},{(s0.BoundingBox.P1.Z + s0.BoundingBox.P2.Z) / 2:F2}) " +
                $"page={s0.LightmapIndex} xy=({s0.X},{s0.Y}) wh=({s0.W},{s0.H})");
        }

        // RED mover surface room assignment + that room's ambient vs level ambient.
        var levelProps = rfl.Sections.Select(s => s.Content).OfType<LevelPropertiesSection>().FirstOrDefault();
        _out.WriteLine($"level ambient = {levelProps?.AmbientColor}");
        foreach (int uid in new[] { 94, 265, 10179 })
        {
            Brush? m = movers.Movers.FirstOrDefault(b => b.Uid == uid);
            if (m is null)
            {
                continue;
            }

            var rooms = m.Geometry.Surfaces.Select(s => s.RoomIndex).Distinct().OrderBy(x => x).ToList();
            _out.WriteLine($"  RED mover {uid}: surfaceRoomIndices=[{string.Join(",", rooms)}]");
            foreach (int ri in rooms)
            {
                if (ri >= 0 && ri < redStatic.Rooms.Count)
                {
                    Room rm = redStatic.Rooms[ri];
                    _out.WriteLine($"    room {ri}: id={rm.Id} hasAmbient={rm.HasAmbientLight} ambient={rm.AmbientColor} sub={rm.IsSubroom}");
                }
            }
        }

        // What GED's static build produces for the atlas (movers excluded).
        CompiledLevel ged = GeometryBuildService.Build(
            rfl, new CompileOptions { Alpine = true, BuildSurfaces = true, BakeLighting = false });
        _out.WriteLine($"GED static build: surfaces={ged.Geometry.Surfaces.Count} lightmapPages={ged.Lightmaps.Count}");
        _out.WriteLine("=> mover surfaces keep RED page indices/coords; GED regenerates the atlas -> stale references.");
    }

    /// <summary>Mean luminance over a surface's texel rect on its lightmap page.</summary>
    private static double SurfaceLuminance(Surface s, IReadOnlyList<Lightmap> pages)
    {
        if (s.LightmapIndex < 0 || s.LightmapIndex >= pages.Count)
        {
            return -1;
        }

        Lightmap p = pages[s.LightmapIndex];
        long sum = 0;
        int count = 0;
        for (int y = 0; y < s.H; y++)
        {
            int py = s.Y + y;
            if (py >= p.Height)
            {
                break;
            }

            for (int x = 0; x < s.W; x++)
            {
                int px = s.X + x;
                if (px >= p.Width)
                {
                    break;
                }

                int o = ((py * p.Width) + px) * 3;
                if (o + 2 < p.Pixels.Length)
                {
                    sum += p.Pixels[o] + p.Pixels[o + 1] + p.Pixels[o + 2];
                    count++;
                }
            }
        }

        return count == 0 ? -1 : (double)sum / (count * 3);
    }
}

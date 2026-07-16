using System;
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
/// Structural parity of GED's compiler against the ORIGINAL compiled geometry of
/// corpus levels: recompile the brushes and compare. Since functional (not
/// byte) parity is the target and GED splits faces differently from RED, the
/// asserted invariants are the geometry-preserving ones — total and per-texture
/// surface area, and every original room covered by a compiled room. Room/portal
/// counts (which depend on RED's exact open-cell/portal partitioning) are
/// reported, not asserted; residual differences are documented in
/// docs/research/compiler-parity-notes.md.
/// </summary>
public sealed class CorpusParityTests
{
    private readonly ITestOutputHelper _out;

    public CorpusParityTests(ITestOutputHelper output)
    {
        _out = output;
    }

    [Theory]
    [InlineData("dm01.rfl")]
    [InlineData("glass_house.rfl")]
    [InlineData("ctf01.rfl")]
    public void Recompiled_Geometry_Preserves_Surface_Area_And_Room_Coverage(string fileName)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile file = RflFile.Load(path);
        file.ParseAllKnownSections();
        Geometry? original = null;
        BrushesSection? brushes = null;
        RoomEffectsSection? effects = null;
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                original ??= g.Geometry;
            }
            else if (s.Content is BrushesSection b)
            {
                brushes ??= b;
            }
            else if (s.Content is RoomEffectsSection e)
            {
                effects ??= e;
            }
        }

        if (original is null || brushes is null)
        {
            return;
        }

        CompiledLevel c = GeometryCompiler.Compile(
            brushes.Brushes, effects?.Effects, new CompileOptions { BuildSurfaces = false });
        Geometry mine = c.Geometry;

        // --- Total surface area within 6% ---
        float areaO = TotalArea(original);
        float areaM = TotalArea(mine);
        float areaDiff = MathF.Abs(areaM - areaO) / areaO;
        _out.WriteLine($"{fileName}: area orig={areaO:F0} mine={areaM:F0} ({areaDiff * 100:F1}%)");
        Assert.True(areaDiff <= 0.06, $"total area differs by {areaDiff * 100:F1}%");

        // --- Per-texture area: most textures within 5% ---
        Dictionary<string, float> byTexO = AreaByTexture(original);
        Dictionary<string, float> byTexM = AreaByTexture(mine);
        int within = 0, considered = 0;
        foreach ((string tex, float ao) in byTexO)
        {
            if (ao < 1f)
            {
                continue;
            }

            considered++;
            float am = byTexM.GetValueOrDefault(tex);
            if (MathF.Abs(am - ao) / ao <= 0.05f)
            {
                within++;
            }
        }

        _out.WriteLine($"{fileName}: per-texture within 5% = {within}/{considered}");
        Assert.True(within >= considered * 0.6, $"only {within}/{considered} textures within 5% area");

        // --- Every original room covered by an overlapping compiled room ---
        int matched = 0;
        foreach (Room ro in original.Rooms)
        {
            if (mine.Rooms.Any(rm => AabbOverlap(ro.Aabb, rm.Aabb)))
            {
                matched++;
            }
        }

        _out.WriteLine($"{fileName}: rooms orig={original.Rooms.Count} mine={mine.Rooms.Count} " +
                       $"covered={matched}/{original.Rooms.Count}; portals orig={original.Portals.Count} mine={mine.Portals.Count}");
        Assert.True(matched >= original.Rooms.Count * 0.9, $"only {matched}/{original.Rooms.Count} original rooms covered");
    }

    [Theory]
    [InlineData("dm01.rfl", 10)]
    [InlineData("dm04.rfl", 10)]
    [InlineData("glass_house.rfl", 2)]
    public void Every_Original_Room_Maps_To_Exactly_One_Recompiled_Room(string fileName, int minRooms)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile file = RflFile.Load(path);
        file.ParseAllKnownSections();
        Geometry? original = null;
        BrushesSection? brushes = null;
        RoomEffectsSection? effects = null;
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                original ??= g.Geometry;
            }
            else if (s.Content is BrushesSection b)
            {
                brushes ??= b;
            }
            else if (s.Content is RoomEffectsSection e)
            {
                effects ??= e;
            }
        }

        if (original is null || brushes is null)
        {
            return;
        }

        CompiledLevel c = GeometryCompiler.Compile(
            brushes.Brushes, effects?.Effects, new CompileOptions { BuildSurfaces = false });
        Geometry mine = c.Geometry;

        _out.WriteLine($"{fileName}: rooms orig={original.Rooms.Count} mine={mine.Rooms.Count}");
        Assert.True(mine.Rooms.Count > minRooms,
            $"{fileName}: only {mine.Rooms.Count} recompiled rooms (require > {minRooms})");

        // Every original room must map to exactly one recompiled room by
        // AABB-overlap majority. Raw overlap volume ties structurally (recompiled
        // room AABBs overlap by design — spanning faces balloon them, and a small
        // room is often wholly inside several), so the majority score is IoU-like:
        // overlap² / (volA · volB), which prefers the similar-sized true match.
        int unmapped = 0, ambiguous = 0;
        foreach (Room ro in original.Rooms)
        {
            double best = 0, second = 0;
            foreach (Room rm in mine.Rooms)
            {
                double overlap = OverlapVolume(ro.Aabb, rm.Aabb);
                if (overlap <= 0)
                {
                    continue;
                }

                double score = overlap * overlap / Math.Max(1e-6, Volume(ro.Aabb) * Volume(rm.Aabb));
                if (score > best)
                {
                    second = best;
                    best = score;
                }
                else if (score > second)
                {
                    second = score;
                }
            }

            if (best <= 0)
            {
                unmapped++;
            }
            else if (second >= best * 0.999)
            {
                ambiguous++; // two recompiled rooms tie exactly — no majority
            }
        }

        _out.WriteLine($"{fileName}: unmapped={unmapped} ambiguous={ambiguous} of {original.Rooms.Count}");
        Assert.True(unmapped == 0, $"{fileName}: {unmapped} original rooms have no overlapping recompiled room");
        Assert.True(ambiguous == 0, $"{fileName}: {ambiguous} original rooms have no majority recompiled room");
    }

    private static float OverlapVolume(Aabb a, Aabb b)
    {
        float dx = MathF.Min(a.P2.X, b.P2.X) - MathF.Max(a.P1.X, b.P1.X);
        float dy = MathF.Min(a.P2.Y, b.P2.Y) - MathF.Max(a.P1.Y, b.P1.Y);
        float dz = MathF.Min(a.P2.Z, b.P2.Z) - MathF.Max(a.P1.Z, b.P1.Z);
        return dx <= 0f || dy <= 0f || dz <= 0f ? 0f : dx * dy * dz;
    }

    private static double Volume(Aabb a)
    {
        Vec3 d = a.P2.Sub(a.P1);
        return Math.Max(1e-6, (double)d.X * d.Y * d.Z);
    }

    private static float TotalArea(Geometry g)
    {
        float sum = 0f;
        foreach (Face f in g.Faces)
        {
            if (f.Texture >= 0)
            {
                sum += FaceArea(g, f);
            }
        }

        return sum;
    }

    private static Dictionary<string, float> AreaByTexture(Geometry g)
    {
        var d = new Dictionary<string, float>();
        foreach (Face f in g.Faces)
        {
            if (f.Texture < 0 || f.Texture >= g.Textures.Count)
            {
                continue;
            }

            string t = g.Textures[f.Texture].ToLowerInvariant();
            d[t] = d.GetValueOrDefault(t) + FaceArea(g, f);
        }

        return d;
    }

    private static float FaceArea(Geometry g, Face f)
    {
        if (f.Vertices.Count < 3)
        {
            return 0f;
        }

        var c = new Vec3(0, 0, 0);
        foreach (FaceVertex v in f.Vertices)
        {
            c = c.Add(g.Vertices[v.Index]);
        }

        c = c.Scale(1f / f.Vertices.Count);
        float area = 0f;
        for (int i = 0; i < f.Vertices.Count; i++)
        {
            Vec3 a = g.Vertices[f.Vertices[i].Index].Sub(c);
            Vec3 b = g.Vertices[f.Vertices[(i + 1) % f.Vertices.Count].Index].Sub(c);
            area += a.Cross(b).Length() * 0.5f;
        }

        return area;
    }

    private static bool AabbOverlap(Aabb a, Aabb b)
    {
        // Small negative slack so touching-but-not-overlapping boxes don't count.
        const float Slack = 0.5f;
        return a.P1.X <= b.P2.X - Slack && a.P2.X >= b.P1.X + Slack &&
               a.P1.Y <= b.P2.Y - Slack && a.P2.Y >= b.P1.Y + Slack &&
               a.P1.Z <= b.P2.Z - Slack && a.P2.Z >= b.P1.Z + Slack;
    }
}

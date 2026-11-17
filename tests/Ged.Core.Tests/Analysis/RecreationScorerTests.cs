using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Core.Analysis.Recreation;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Core.Tests.Spatial;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// The recreation scorer's validation cases (recreate/SCORING.md §6): a level against itself scores 100, against a
/// rotated and translated copy of itself at least 99, against an empty level about 0; shrinking the level's pieces
/// lowers the score in order of the shrink; a mockup truth recovers the candidate's scale. Synthetic floors keep these
/// fast; one stock level pair runs as a DeepGate (skipped without the local level copies).
/// </summary>
public sealed class RecreationScorerTests
{
    private readonly ITestOutputHelper _out;

    public RecreationScorerTests(ITestOutputHelper output) => _out = output;

    /// <summary>A floor rectangle (x0, z0)–(x1, z1) at height y; Slab = also an underside 0.2 m lower (a raised deck).</summary>
    private readonly record struct Piece(double X0, double Z0, double X1, double Z1, double Y, bool Slab = false);

    /// <summary>An asymmetric little level: a yard, a corridor, a raised room up a step and a deck over the yard.</summary>
    private static readonly Piece[] Level =
    {
        new(0, 0, 30, 20, 0),
        new(30, 5, 50, 12, 0),
        new(40, 12, 50, 40, 2),
        new(20, 30, 40, 36, 2),
        new(5, 5, 15, 15, 4, Slab: true),
    };

    private static Geometry Build(IEnumerable<Piece> pieces, double yawDeg = 0, double tx = 0, double ty = 0, double tz = 0, double shrink = 0)
    {
        double a = yawDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        var g = new Geometry();
        g.Textures.Add("floor.tga");
        void Quad(Piece p, double y, bool up)
        {
            double cx = (p.X0 + p.X1) / 2, cz = (p.Z0 + p.Z1) / 2, k = 1 - shrink;
            var corners = new[] { (p.X0, p.Z0), (p.X1, p.Z0), (p.X1, p.Z1), (p.X0, p.Z1) }
                .Select(q => (cx + ((q.Item1 - cx) * k), cz + ((q.Item2 - cz) * k)))
                .Select(q => new Vec3((float)((c * q.Item1) - (s * q.Item2) + tx), (float)(y + ty), (float)((s * q.Item1) + (c * q.Item2) + tz)))
                .ToList();
            var f = new Face { Texture = 0, RoomIndex = -1, Plane = new RfPlane(new Vec3(0, up ? 1 : -1, 0), (float)(up ? -(y + ty) : y + ty)) };
            foreach (Vec3 v in up ? corners : Enumerable.Reverse(corners))
            {
                f.Vertices.Add(new FaceVertex { Index = g.Vertices.Count });
                g.Vertices.Add(v);
            }

            g.Faces.Add(f);
        }

        foreach (Piece p in pieces)
        {
            Quad(p, p.Y, up: true);
            if (p.Slab)
            {
                Quad(p, p.Y - 0.2, up: false);
            }
        }

        return g;
    }

    private RecreationScoreResult Score(Geometry truth, Geometry candidate, bool mirror = false)
    {
        RecreationScoreResult r = RecreationScorer.ScoreAgainstLevel(candidate, null, truth, null, mirror, CancellationToken.None);
        _out.WriteLine($"score {r.Score:0.00}: {r.Report["components"]!.ToJsonString()} alignment {r.Report["alignment"]?.ToJsonString()}");
        return r;
    }

    [Fact]
    public void Truth_Against_Itself_Scores_100()
    {
        RecreationScoreResult r = Score(Build(Level), Build(Level));
        Assert.Equal(100, r.Score, 2);
        // Floors at 0, 2 and 4 m: three height bands.
        Assert.Equal(3, r.Report["plan"]!["bands"]!.AsArray().Count);
        Assert.True(r.Images.ContainsKey("overlay_plan.png"));
    }

    [Fact]
    public void Rotated_And_Translated_Truth_Scores_At_Least_99()
    {
        RecreationScoreResult r = Score(Build(Level), Build(Level, yawDeg: 37, tx: 123.4, ty: 7, tz: -56.7));
        Assert.True(r.Score >= 99, $"score {r.Score}");
        // The candidate is rotated +37° about +Y in x-z; in the plan frame (x, −z) that is −37°.
        double yaw = r.Report["alignment"]!["yawDegrees"]!.GetValue<double>();
        Assert.True(Math.Abs(((yaw - 37 + 540) % 360) - 180) <= 1.01, $"yaw {yaw}");
        Assert.False(r.Report["alignment"]!["mirrored"]!.GetValue<bool>());
        Assert.Equal(-7, r.Report["heights"]!["verticalOffset"]!.GetValue<double>(), 1);
    }

    [Fact]
    public void Empty_Candidate_Scores_About_0()
    {
        RecreationScoreResult r = Score(Build(Level), new Geometry());
        Assert.True(r.Score <= 1, $"score {r.Score}");
    }

    [Fact]
    public void Shrinking_The_Pieces_Lowers_The_Score_In_Order()
    {
        Geometry truth = Build(Level);
        double s5 = Score(truth, Build(Level, shrink: 0.05)).Score;
        double s10 = Score(truth, Build(Level, shrink: 0.10)).Score;
        double s25 = Score(truth, Build(Level, shrink: 0.25)).Score;
        Assert.True(s5 < 100 && s10 < s5 && s25 < s10, $"5% {s5}, 10% {s10}, 25% {s25}");
        Assert.True(s10 < 95 && s10 > 50, $"10% shrink scored {s10}");
    }

    [Fact]
    public void Mirror_Is_Only_Found_When_Allowed()
    {
        // The level mirrored (x → −x) and moved.
        Piece[] mirrored = Level.Select(p => p with { X0 = -p.X1, X1 = -p.X0 }).ToArray();
        RecreationScoreResult plain = Score(Build(Level), Build(mirrored, tx: 20));
        RecreationScoreResult allowed = Score(Build(Level), Build(mirrored, tx: 20), mirror: true);
        Assert.True(allowed.Report["alignment"]!["mirrored"]!.GetValue<bool>());
        Assert.True(allowed.Score >= 99, $"mirrored with --allow-mirror scored {allowed.Score}");
        Assert.True(plain.Score < allowed.Score - 5, $"without mirror {plain.Score}, with {allowed.Score}");
    }

    [Fact]
    public void Mockup_Truth_Recovers_Scale_Route_And_Markers()
    {
        // A "mockup" of the level drawn at 3 px/m, rotated 90°: image u = 3·(40 − z) + 20, v = 3·(50 − x) + 20.
        const int W = 160, H = 200;
        static (double U, double V) Px(double x, double z) => ((3 * (40 - z)) + 20, (3 * (50 - x)) + 20);
        var rgba = new byte[W * H * 4];
        for (int v = 0; v < H; v++)
        {
            for (int u = 0; u < W; u++)
            {
                double z = 40 - ((u + 0.5 - 20) / 3), x = 50 - ((v + 0.5 - 20) / 3);
                bool on = Level.Any(p => x >= p.X0 && x < p.X1 && z >= p.Z0 && z < p.Z1);
                int q = ((v * W) + u) * 4;
                rgba[q] = rgba[q + 1] = rgba[q + 2] = on ? (byte)255 : (byte)0;
                rgba[q + 3] = 255;
            }
        }

        string dir = Path.Combine(Path.GetTempPath(), "ged-recreate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "playable_mask.png"), PngWriter.Encode(W, H, rgba));
            // Route: yard → corridor → up into the raised room.
            (double, double)[] route = { Px(3, 10), Px(28, 10), Px(45, 8), Px(45, 38) };
            var json = new JsonObject
            {
                ["kind"] = "mockup",
                ["name"] = "synthetic",
                ["mapCrop"] = new JsonArray(0, 0, W, H),
                ["playableMask"] = "playable_mask.png",
                ["route"] = new JsonArray(route.Select(p => (JsonNode)new JsonArray(p.Item1, p.Item2)).ToArray()),
                ["points"] = new JsonArray(
                    new JsonObject { ["label"] = "A", ["at"] = new JsonArray(Px(3, 10).U, Px(3, 10).V) },
                    new JsonObject { ["label"] = "1", ["at"] = new JsonArray(Px(25, 33).U, Px(25, 33).V) },
                    new JsonObject { ["label"] = "B", ["at"] = new JsonArray(Px(45, 38).U, Px(45, 38).V) }),
                ["profile"] = new JsonObject { ["curve"] = new JsonArray(new JsonArray(0, 0), new JsonArray(0.6, 0), new JsonArray(0.75, 1), new JsonArray(1, 1)) },
            };
            string truthPath = Path.Combine(dir, "truth.json");
            File.WriteAllText(truthPath, json.ToJsonString());
            RecreationScoreResult r = RecreationScorer.ScoreAgainstMockup(Build(Level), null, truthPath, false, CancellationToken.None);
            _out.WriteLine(r.Report.ToJsonString());
            Assert.Equal(3, r.Report["alignment"]!["scale"]!.GetValue<double>(), 0.15);
            Assert.True(r.Report["route"]!["coverage"]!.GetValue<double>() >= 0.95);
            Assert.Equal(3, r.Report["route"]!["markersOnFloor"]!.GetValue<int>());
            Assert.True(r.Report["route"]!["profileCorrelation"]!.GetValue<double>() > 0.8);
            Assert.True(r.Score >= 90, $"score {r.Score}");
            Assert.True(r.Images.ContainsKey("overlay_mockup.png"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>dm02 against itself, a rotated + translated copy, and ctf02 (the cross-level score is recorded, not pinned).</summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public void Stock_Levels_Score_As_Expected()
    {
        if (StudyData.LevelsDir is not { } dir || !File.Exists(Path.Combine(dir, "dm02.rfl")) || !File.Exists(Path.Combine(dir, "ctf02.rfl")))
        {
            return;
        }

        Geometry Load(string name)
        {
            RflFile rfl = RflFile.Load(Path.Combine(dir, name));
            rfl.ParseAllKnownSections();
            return rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;
        }

        Geometry dm02 = Load("dm02.rfl");
        Assert.Equal(100, Score(dm02, Load("dm02.rfl")).Score, 2);

        Geometry moved = Load("dm02.rfl");
        double a = 37 * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        for (int i = 0; i < moved.Vertices.Count; i++)
        {
            Vec3 v = moved.Vertices[i];
            moved.Vertices[i] = new Vec3((float)((c * v.X) - (s * v.Z) + 31.7), v.Y + 5f, (float)((s * v.X) + (c * v.Z) - 12.3));
        }

        foreach (Face f in moved.Faces.Where(f => f.Vertices.Count > 0))
        {
            Vec3 n = f.Plane.Normal;
            var rn = new Vec3((float)((c * n.X) - (s * n.Z)), n.Y, (float)((s * n.X) + (c * n.Z)));
            Vec3 p = moved.Vertices[f.Vertices[0].Index];
            f.Plane = new RfPlane(rn, -((rn.X * p.X) + (rn.Y * p.Y) + (rn.Z * p.Z)));
        }

        double rotated = Score(dm02, moved).Score;
        Assert.True(rotated >= 99, $"dm02 rotated 37° scored {rotated}");

        double cross = Score(dm02, Load("ctf02.rfl")).Score;
        _out.WriteLine($"ctf02 as a recreation of dm02: {cross:0.0}");
        Assert.True(cross < 40, $"ctf02 vs dm02 scored {cross}");
    }
}

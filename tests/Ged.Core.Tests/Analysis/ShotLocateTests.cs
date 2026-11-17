using System;
using System.Globalization;
using System.IO;
using Ged.Core.Analysis.Recreation;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Ged.Core.Tests.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// The eye-level screenshot tools: ref.shot_locate finds a known camera in a traced build from a picture taken there,
/// ref.shot_measure reads a ledge height along a pixel's ray, and ref.shot_compare writes the side-by-side picture.
/// </summary>
public sealed class ShotLocateTests
{
    private const string Level = """
        local kit = ged.import("kit")
        kit.trace{ name = "shots", via = "prism", regions = { -- (the bare plan: the shot tools measure its plain faces)
          { name = "hall", poly = {{0, 0}, {24, 0}, {24, 16}, {0, 16}}, floor = 0, ceiling = 8 },
          { name = "gallery", kind = "deck", poly = {{16, 0}, {24, 0}, {24, 16}, {16, 16}}, floor = 3 },
          { name = "side", kind = "corridor", poly = {{-14, 5}, {0, 5}, {0, 9}, {-14, 9}}, floor = 0, height = 3.5 },
          { name = "alcove", poly = {{6, 16}, {12, 16}, {12, 22}, {6, 22}}, floor = 1, height = 3 } },
          solids = { { circle = {7, 6, 0.6}, top = "ceiling" }, { poly = {{10, 10}, {13, 10}, {13, 12}, {10, 12}}, height = 1.2 } } }
        ops.build()
        return "ok"
        """;

    private readonly ITestOutputHelper _out;

    public ShotLocateTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Locate_Finds_The_Camera_And_Measure_Reads_A_Ledge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ged-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
            Run(doc, Level);

            // The "screenshot": the build seen from a known pose (ray-cast shading), 320 x 240, 90 degrees.
            var truth = new ShotPose(4.5, 1.7, 3.0, 62, 4, 90);
            ShotScene scene = ShotScene.Of(SpatialLevel.StoredGeometry(doc.Rfl)!, default);
            byte[] rgba = ShotScene.Shade(scene.Perspective(truth, 320, 240), null);
            string shot = Path.Combine(dir, "shot.png");
            File.WriteAllBytes(shot, Ged.Core.IO.Tex.PngWriter.Encode(320, 240, rgba));

            // The deck's front face at 2.75 m, 6 m ahead: its pixel.
            Assert.True(truth.Project(new Ged.Core.Editing.Organic.Vec3d(16, 2.75, 6.5).Sub(new(truth.X, truth.Y, truth.Z)), 320, 240, out double u, out double v));

            string script = $$"""
                local l = ref.shot_locate{shot = [[{{shot}}]], fov = 90, out = [[{{Path.Combine(dir, "loc.png")}}]]}
                local b = l.best
                local m = ref.shot_measure{pose = b, shot = [[{{shot}}]], px = {{{F(u)}}, {{F(v)}}}, distance = 5}
                local c = ref.shot_compare{shot = [[{{shot}}]], pose = {x = 4.5, z = 3, yaw = 62, pitch = 4}, edges = true, out = [[{{Path.Combine(dir, "cmp.png")}}]]}
                return string.format("%.3f %.3f %.3f %.2f %.4f %.3f %s %.4f %d %.3f %.2f", b.x, b.z, b.yaw, b.pitch, b.score, m.height, m.surface, c.score, l.positions, m.at_distance, l.seconds)
                """;
            string[] r = Run(doc, script).Split(' ');
            double x = Num(r[0]), z = Num(r[1]), yaw = Num(r[2]);
            double pos = Math.Sqrt(((x - truth.X) * (x - truth.X)) + ((z - truth.Z) * (z - truth.Z)));
            double dyaw = Math.Abs(((yaw - truth.Yaw + 540) % 360) - 180);
            _out.WriteLine($"position error {pos:0.00} m, yaw error {dyaw:0.0} deg, seconds {r[10]}");
            Assert.True(pos < 2, $"position error {pos} m");
            Assert.True(dyaw < 10, $"yaw error {dyaw} deg");
            Assert.InRange(Num(r[5]), 2.45, 3.05); // the ledge face, measured from the located pose
            Assert.Equal("wall", r[6]);
            Assert.True(Num(r[7]) > 0.6, "the true pose matches its own shot: " + r[7]);
            Assert.True(File.Exists(Path.Combine(dir, "loc.png")) && File.Exists(Path.Combine(dir, "cmp.png")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Measure_Without_A_Build_Gives_The_Ray_And_The_Height_At_A_Distance()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        // a pixel 45 degrees up at the centre column: at 4 m away it is 1.7 + 4 = 5.7 m up
        string r = Run(doc, "local m = ref.shot_measure{pose = {x = 0, y = 1.7, z = 0, yaw = 0, pitch = 0, fov = 90}, size = {200, 200}, px = {100, 0}, distance = 4} return string.format('%.3f %.2f', m.at_distance_y, m.elevation)");
        Assert.Equal("5.700 45.00", r);
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private string Run(EditorDocument doc, string script)
    {
        ScriptRunResult r = ScriptTestFixture.Runner().Run(Services(doc), script, Options(), new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        _out.WriteLine(r.ReturnValue);
        return r.ReturnValue!;
    }

    private static ScriptServices Services(EditorDocument doc) => new()
    {
        Document = doc,
        Confirmation = new AllowAllConfirmation(),
        FileReadAccess = (p, _) => p,
        FileWriteAccess = (p, _) => p,
    };

    private static ScriptRunOptions Options() => new()
    {
        ChunkName = "shot-test",
        AllowDestructive = true,
        Limits = new ScriptExecutionLimits { Timeout = TimeSpan.FromSeconds(300), InstructionBudget = 2_000_000_000 },
    };
}

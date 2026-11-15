using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Automation.Reference;
using Ged.Automation.Rendering;
using Ged.Automation.Tests.Mcp;
using Ged.Cli;
using Ged.Cli.Mcp;
using Ged.Rendering.Graphics;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// Reference matching (<c>glacier-cli ref</c>, the <c>reference_*</c> tools): crop and snap math, the reference set,
/// the camera math and single-view estimates, the picture cues and hints, the render's hfov / eyeHeight, and the
/// comparison end to end through the CLI and the MCP server (GPU tests skip without a device). Sheets are written to
/// tests/artifacts/reference for inspection.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class ReferenceTests
{
    private static readonly string ArtifactsDir = InitArtifacts();

    private static string InitArtifacts()
    {
        string dir = TestLevels.RepoRoot is { } root
            ? Path.Combine(root, "tests", "artifacts", "reference")
            : Path.Combine(Path.GetTempPath(), "ged-reference-artifacts");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GraphicsDevice? TryDevice()
    {
        try
        {
            return RenderDevices.Create().Device;
        }
        catch (AutomationException)
        {
            return null;
        }
    }

    private static CliEnvironment Env(GraphicsDevice? device) => new()
    {
        PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Clock = () => TestLevels.Fixed,
        RenderDevice = device is null ? null : () => (device, "test device"),
    };

    private static ReferenceIo Io() => new() { RequireWrite = PathAllowList.Normalize };

    /// <summary>
    /// A composite-image panel: a dark background, a 2 px light frame whose outer edge is (40, 30)-(240, 170), and a
    /// dark textured picture inside it (42, 32)-(238, 168).
    /// </summary>
    private static string WritePanel(string path)
    {
        const int w = 300, h = 200;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                int v = 20;
                if (x >= 40 && x < 240 && y >= 30 && y < 170)
                {
                    v = 200;
                }

                if (x >= 42 && x < 238 && y >= 32 && y < 168)
                {
                    v = 30 + ((x - 42) * 40 / 196) + ((y - 32) * 20 / 136) + (((x * 7) + (y * 13)) % 5);
                }

                px[i] = px[i + 1] = px[i + 2] = (byte)v;
                px[i + 3] = 255;
            }
        }

        File.WriteAllBytes(path, Ged.Core.IO.Tex.PngWriter.Encode(w, h, px));
        return path;
    }

    // ---- crop, snap and the reference set ----------------------------------------------------------------

    [Fact]
    public void Add_Crops_Snaps_Onto_The_Frame_And_Stores_The_Picture()
    {
        using var dir = new TempDir();
        string panel = WritePanel(dir.File("concept.png"));
        string refs = dir.File("refs");
        AutomationResponse r = ReferenceCommand.Execute(new JsonObject
        {
            ["op"] = "add", ["refs"] = refs, ["image"] = panel, ["id"] = "yard", ["crop"] = "37,28,205,146", ["snap"] = true,
            ["area"] = "The Yard", ["notes"] = "walls and walkways",
        }, Io());
        Assert.True(r.Ok, r.Error?.Message);
        JsonNode rec = r.Result!["reference"]!;
        Assert.Equal(new[] { 42, 32, 196, 136 }, rec["crop"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal(196, rec["width"]!.GetValue<int>());
        Assert.Equal(136, rec["height"]!.GetValue<int>());
        Assert.Equal(new[] { 37, 28, 205, 146 }, r.Result!["requestedCrop"]!.AsArray().Select(n => n!.GetValue<int>()));
        (int pw, int ph, byte[] pixels) = PngTestReader.Read(Path.Combine(refs, "yard.png"));
        Assert.Equal((196, 136), (pw, ph));
        Assert.True(pixels.Where((_, i) => i % 4 == 0).All(v => v is >= 30 and < 100), "the stored picture contains frame or background pixels");

        // Without snap the crop is taken as given; an id clash needs replace; the set lists both.
        Assert.False(ReferenceCommand.Execute(new JsonObject { ["op"] = "add", ["refs"] = refs, ["image"] = panel, ["id"] = "yard" }, Io()).Ok);
        AutomationResponse plain = ReferenceCommand.Execute(new JsonObject
        {
            ["op"] = "add", ["refs"] = refs, ["image"] = panel, ["id"] = "raw", ["crop"] = new JsonArray(37, 28, 205, 146),
        }, Io());
        Assert.True(plain.Ok, plain.Error?.Message);
        Assert.Equal(205, plain.Result!["reference"]!["width"]!.GetValue<int>());
        JsonNode list = ReferenceCommand.Execute(new JsonObject { ["op"] = "list", ["refs"] = refs }, Io()).Result!;
        Assert.Equal(2, list["count"]!.GetValue<int>());
        Assert.Equal(new[] { "yard", "raw" }, list["references"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        Assert.Equal("The Yard", list["references"]![0]!["area"]!.GetValue<string>());

        // Bad requests are usage errors.
        foreach (JsonObject bad in new[]
        {
            new JsonObject { ["op"] = "add", ["refs"] = refs, ["image"] = panel, ["id"] = "bad id" },
            new JsonObject { ["op"] = "add", ["refs"] = refs, ["image"] = panel, ["id"] = "x", ["crop"] = "290,190,20,20" },
            new JsonObject { ["op"] = "add", ["refs"] = refs, ["image"] = panel, ["id"] = "x", ["snap"] = true },
            new JsonObject { ["op"] = "add", ["refs"] = refs, ["image"] = dir.File("notes.txt"), ["id"] = "x" },
        })
        {
            AutomationResponse f = ReferenceCommand.Execute(bad, Io());
            Assert.False(f.Ok);
            Assert.Equal(AutomationErrorCodes.Usage, f.Error!.Code);
        }
    }

    [Fact]
    public void View_Draws_Deterministic_Grid_Images()
    {
        using var dir = new TempDir();
        string panel = WritePanel(dir.File("concept.png"));
        string refs = dir.File("refs");
        JsonNode source = ReferenceCommand.Execute(new JsonObject { ["op"] = "view", ["image"] = panel, ["out"] = dir.File("a.png"), ["size"] = 600 }, Io()).Result!;
        JsonNode again = ReferenceCommand.Execute(new JsonObject { ["op"] = "view", ["image"] = panel, ["out"] = dir.File("b.png"), ["size"] = 600 }, Io()).Result!;
        Assert.Equal("source", source["kind"]!.GetValue<string>());
        Assert.Equal(File.ReadAllBytes(dir.File("a.png")), File.ReadAllBytes(dir.File("b.png")));
        Assert.Equal(again["width"]!.GetValue<int>(), PngTestReader.Read(dir.File("a.png")).Width);

        Assert.True(ReferenceCommand.Execute(new JsonObject { ["op"] = "add", ["refs"] = refs, ["image"] = panel, ["id"] = "p", ["crop"] = "42,32,196,136" }, Io()).Ok);
        Assert.True(ReferenceCommand.Execute(new JsonObject { ["op"] = "camera", ["refs"] = refs, ["id"] = "p", ["horizon"] = "0.4", ["vp"] = "0.6,0.4" }, Io()).Ok);
        JsonNode marked = ReferenceCommand.Execute(new JsonObject { ["op"] = "view", ["refs"] = refs, ["id"] = "p", ["out"] = dir.File("m.png"), ["size"] = 500 }, Io()).Result!;
        Assert.Equal("reference", marked["kind"]!.GetValue<string>());
        File.Copy(dir.File("m.png"), Path.Combine(ArtifactsDir, "marked_reference.png"), overwrite: true);
    }

    // ---- camera math ---------------------------------------------------------------------------------------

    [Fact]
    public void Field_Of_View_And_Horizon_Math()
    {
        Assert.Equal(53.130, ReferenceCamera.VerticalFov(90, 2.0), 3);
        Assert.Equal(1.0, ReferenceCamera.FocalHeights(90, 2.0), 6);
        Assert.Equal(53.130, RenderSpec.VerticalFov(90, 200, 100), 2);
        Assert.Equal(90, RenderSpec.HorizontalFov(RenderSpec.VerticalFov(90, 1280, 720), 1280, 720), 2);
        Assert.Equal(0.5, ReferenceCamera.HorizonRow(0, 90, 2.0), 9);
        Assert.Equal(0.6, ReferenceCamera.HorizonRow(Math.Atan(0.1) * 180 / Math.PI, 90, 2.0), 9);
        Assert.Equal(90, ReferenceCamera.Heading(Vector3.UnitX), 6);
        Assert.Equal(0, ReferenceCamera.Heading(Vector3.UnitZ), 6);
        Assert.Equal(45, ReferenceCamera.Pitch(new Vector3(0, 1, 1)), 4);
        Assert.Equal(1.7, ReferenceCamera.ParseHeight("eye"));
        Assert.Equal(2.25, ReferenceCamera.ParseHeight("2.25"));
        Assert.Throws<AutomationException>(() => ReferenceCamera.ParseHeight("tall"));
    }

    [Fact]
    public void Nudges_Move_In_The_Camera_Frame_And_Keep_The_Look_Distance()
    {
        (Vector3 eye, Vector3 look) = ReferenceCamera.Nudge(Vector3.Zero, new Vector3(0, 0, 10), new Vector3(2, 1, 0.5f), Vector2.Zero);
        Assert.True(Vector3.Distance(new Vector3(1, 0.5f, 2), eye) < 1e-5f, eye.ToString());
        Assert.True(Vector3.Distance(eye + new Vector3(0, 0, 10), look) < 1e-4f, look.ToString());

        (eye, look) = ReferenceCamera.Nudge(Vector3.Zero, new Vector3(0, 0, 10), Vector3.Zero, new Vector2(90, 10));
        Assert.Equal(90, ReferenceCamera.Heading(look - eye), 3);
        Assert.Equal(10, ReferenceCamera.Pitch(look - eye), 3);
        Assert.Equal(10, Vector3.Distance(eye, look), 3);

        // Facing +X, "right" is -Z (left-handed, +Y up).
        (eye, _) = ReferenceCamera.Nudge(Vector3.Zero, new Vector3(10, 0, 0), new Vector3(0, 3, 0), Vector2.Zero);
        Assert.True(Vector3.Distance(new Vector3(0, 0, -3), eye) < 1e-5f, eye.ToString());
    }

    [Fact]
    public void Estimates_Recover_A_Known_Camera_From_Its_Marks()
    {
        // hfov 90 at aspect 2: the focal length is exactly one picture height.
        CameraEstimate pitch = ReferenceCamera.Estimate(new ReferenceMarks { Horizon = new[] { 0.6 } }, 2.0, 90, null);
        Assert.Equal(Math.Atan(0.1) * 180 / Math.PI, pitch.PitchDeg!.Value, 1);

        // A level camera, eye 1.7 m, a 2.5 m door 10 m away at the centre column: base row 0.67, top row 0.42.
        CameraEstimate known = ReferenceCamera.Estimate(new ReferenceMarks
        {
            Horizon = new[] { 0.5 },
            Object = new[] { 0.5, 0.67, 0.42, 2.5 },
            Measure = new[] { 0.5, 0.67, 0.42, 0.5, 0.67, 0.27 },
        }, 2.0, 90, null);
        Assert.Equal(1.7, known.EyeHeight!.Value, 2);
        Assert.Equal(10, known.ObjectDistance!.Value, 2);
        Assert.Equal("object", known.MeasureEyeHeightSource);
        Assert.Equal(2.5, known.Measurements![0].Height!.Value, 2);
        Assert.Equal(4.0, known.Measurements[1].Height!.Value, 2); // a 4 m wall at the same distance

        // A vanishing point 20 degrees right of the heading (u = tan 20), lines running at world heading 50: camera heading 30.
        double x = 0.5 + (Math.Tan(20 * Math.PI / 180) / 2.0);
        double dir = 50 * Math.PI / 180;
        CameraEstimate vp = ReferenceCamera.Estimate(new ReferenceMarks { Vp = new[] { x, 0.5 }, VpDir = new[] { Math.Sin(dir), Math.Cos(dir) } }, 2.0, 90, null);
        Assert.Equal(20, vp.VpOffsetDeg!.Value, 1);
        Assert.Equal(30, vp.HeadingDeg!.Value, 1);
        Assert.Equal(0, vp.PitchDeg!.Value, 3);

        // A horizon falling to the right through the centre: roll, no pitch.
        CameraEstimate roll = ReferenceCamera.Estimate(new ReferenceMarks { Horizon = new[] { 0.0, 0.45, 1.0, 0.55 } }, 2.0, 90, null);
        Assert.Equal(Math.Atan(0.05) * 180 / Math.PI, roll.RollDeg!.Value, 1);
        Assert.Equal(0, roll.PitchDeg!.Value, 2);

        Assert.Throws<AutomationException>(() => ReferenceCamera.Estimate(new ReferenceMarks { Object = new[] { 0.5, 0.6 } }, 2, 90, null));
    }

    [Fact]
    public void Camera_Operation_Sets_Nudges_Applies_Estimates_And_Persists()
    {
        using var dir = new TempDir();
        string refs = dir.File("refs");
        Assert.True(ReferenceCommand.Execute(new JsonObject { ["op"] = "add", ["refs"] = refs, ["image"] = WritePanel(dir.File("c.png")), ["id"] = "a", ["crop"] = "42,32,196,98" }, Io()).Ok);

        // No camera yet: move / turn and estimates without a position are usage errors.
        Assert.Equal(AutomationErrorCodes.Usage, ReferenceCommand.Execute(new JsonObject { ["op"] = "camera", ["refs"] = refs, ["id"] = "a", ["move"] = "1,0,0" }, Io()).Error!.Code);

        JsonNode set = ReferenceCommand.Execute(new JsonObject
        {
            ["op"] = "camera", ["refs"] = refs, ["id"] = "a", ["camera"] = "0,1.7,0", ["heading"] = 90, ["pitch"] = 0, ["eyeHeight"] = "eye",
        }, Io()).Result!;
        Assert.Equal(90, set["camera"]!["heading"]!.GetValue<double>(), 3);
        Assert.Equal(1.7, set["camera"]!["eyeHeight"]!.GetValue<double>(), 3);
        Assert.Equal(90, set["camera"]!["hfov"]!.GetValue<double>(), 3);
        Assert.Contains("--hfov 90", set["renderFlags"]!.GetValue<string>(), StringComparison.Ordinal);

        JsonNode nudged = ReferenceCommand.Execute(new JsonObject { ["op"] = "camera", ["refs"] = refs, ["id"] = "a", ["move"] = "2,0,0", ["turn"] = "-90,0" }, Io()).Result!;
        Assert.Equal(new[] { 2.0, 1.7, 0.0 }, nudged["camera"]!["position"]!.AsArray().Select(n => Math.Round(n!.GetValue<double>(), 3)));
        Assert.Equal(0, nudged["camera"]!["heading"]!.GetValue<double>(), 3);

        // Marks: the horizon at 0.6 of a 2:1 picture at hfov 90 is a pitch of atan(0.1) up; apply puts it on the camera.
        JsonNode applied = ReferenceCommand.Execute(new JsonObject
        {
            ["op"] = "camera", ["refs"] = refs, ["id"] = "a", ["horizon"] = "0.6", ["applyEstimate"] = true,
        }, Io()).Result!;
        Assert.Equal(Math.Round(Math.Atan(0.1) * 180 / Math.PI, 2), applied["estimate"]!["pitchDeg"]!.GetValue<double>(), 2);
        Assert.Equal(Math.Atan(0.1) * 180 / Math.PI, applied["camera"]!["pitch"]!.GetValue<double>(), 1);

        // Persisted: a fresh load sees the camera and the marks; clear_marks removes them.
        ReferenceRecord stored = ReferenceStore.Load(refs).Get("a");
        Assert.Equal(2, stored.Camera!.Position[0], 3);
        Assert.Equal(0.6, stored.Marks!.Horizon![0], 6);
        Assert.True(ReferenceCommand.Execute(new JsonObject { ["op"] = "camera", ["refs"] = refs, ["id"] = "a", ["clearMarks"] = true }, Io()).Ok);
        Assert.Null(ReferenceStore.Load(refs).Get("a").Marks);
    }

    [Fact]
    public void Scale_Converts_Counted_Units_From_The_Measured_Table()
    {
        JsonNode r = ReferenceCommand.Execute(new JsonObject { ["op"] = "scale", ["count"] = 3, ["unit"] = "door_height" }, Io()).Result!;
        Assert.Equal(7.5, r["conversion"]!["metres"]!.GetValue<double>(), 3);
        Assert.Contains(r["cues"]!.AsArray(), c => c!["unit"]!.GetValue<string>() == "stair_riser" && c["metres"]!.GetValue<double>() == 0.25);
        Assert.Equal(2.0, ReferenceScale.Find("door-widths").Metres);
        Assert.False(ReferenceCommand.Execute(new JsonObject { ["op"] = "scale", ["count"] = 2, ["unit"] = "giraffe" }, Io()).Ok);
    }

    // ---- cues ------------------------------------------------------------------------------------------------

    [Fact]
    public void Cues_Measure_Edges_Plain_Top_And_Give_Hints()
    {
        // Top half plain, bottom half vertical stripes 4 px wide: a plain top of about one half, edges only below.
        static RasterImage Striped(bool stripes)
        {
            var img = RasterImage.Filled(200, 100, 60, 60, 60);
            for (int y = 50; y < 100; y++)
            {
                for (int x = 0; x < 200; x++)
                {
                    byte v = stripes && (x / 4) % 2 == 0 ? (byte)200 : (byte)60;
                    int i = ((y * 200) + x) * 4;
                    img.Pixels[i] = img.Pixels[i + 1] = img.Pixels[i + 2] = v;
                }
            }

            return img;
        }

        RasterImage busy = Striped(true), plain = Striped(false);
        bool[] be = ImageCues.Edges(busy), pe = ImageCues.Edges(plain);
        bool[] none = new bool[200 * 100];
        PictureCues bc = ImageCues.Measure(busy, be, none, 0.5);
        PictureCues pc = ImageCues.Measure(plain, pe, none, null);
        Assert.InRange(bc.PlainTop, 0.45, 0.52);
        Assert.Equal(0, bc.EdgeGrid[0].Sum());
        Assert.True(bc.EdgeGrid[2].Average() > 0.2, "the striped band has edges");
        Assert.Equal(0, pc.EdgeDensity);
        Assert.Equal(1.0, ImageCues.Overlap(be, be, none, 200, 100));

        // An ignored rectangle leaves its pixels out.
        bool[] ignore = ImageCues.IgnoreMask(200, 100, new[] { 0.0, 0.4, 1.0, 0.6 }); // the stripes and their top edge
        Assert.Equal(0, ImageCues.Measure(busy, be, ignore, null).EdgeDensity);

        List<string> hints = ImageCues.Hints(bc, pc, 90, 2.0, 0);
        Assert.Contains(hints, h => h.StartsWith("DETAIL: the render is plainer", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_Spec_Takes_Hfov_And_Eye_Height_On_Camera_Views_Only()
    {
        RenderSpec ok = RenderSpec.FromArgs(new JsonObject { ["out"] = "a.png", ["camera"] = "0,2,0", ["look"] = "0,2,1", ["hfov"] = 90, ["eyeHeight"] = 1.7 });
        Assert.Equal(90f, ok.HFovDegrees);
        Assert.Equal(1.7f, ok.EyeHeight);
        foreach (JsonObject bad in new[]
        {
            new JsonObject { ["out"] = "a.png", ["camera"] = "0,2,0", ["look"] = "0,2,1", ["hfov"] = 90, ["fov"] = 60 },
            new JsonObject { ["out"] = "a.png", ["hfov"] = 90 },
            new JsonObject { ["out"] = "a.png", ["eyeHeight"] = 1.7, ["view"] = "iso" },
            new JsonObject { ["out"] = "a.png", ["camera"] = "0,2,0", ["look"] = "0,2,1", ["hfov"] = 200 },
        })
        {
            Assert.Throws<AutomationException>(() => RenderSpec.FromArgs(bad));
        }
    }

    [Fact]
    public void Agent_Guide_Ref_Commands_Parse()
    {
        if (TestLevels.RepoRoot is not { } root)
        {
            return;
        }

        string guide = File.ReadAllText(Path.Combine(root, "docs", "ai", "AGENT-GUIDE.md"));
        var commands = System.Text.RegularExpressions.Regex.Matches(guide, "`(glacier-cli ref [^`]+)`")
            .Select(m => m.Groups[1].Value).Where(c => !c.Contains('|', StringComparison.Ordinal)).ToList();
        Assert.True(commands.Count >= 6, $"the guide's reference workflow lost its commands ({commands.Count})");
        foreach (string command in commands)
        {
            // Split like a shell: double quotes group words.
            var argv = System.Text.RegularExpressions.Regex.Matches(command, "\"([^\"]*)\"|(\\S+)")
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Skip(1).ToArray();
            CliArguments parsed = CliArguments.Parse(argv); // throws for an unknown option
            Assert.Equal("ref", parsed.Verb);
            Assert.Contains(parsed.Positionals[0], ReferenceCommand.Ops);
        }
    }

    // ---- end to end (GPU) ---------------------------------------------------------------------------------

    [Fact]
    public async Task Eye_Height_Stands_The_Camera_On_The_Floor_And_Hfov_Sets_The_Vertical_Fov()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (int code, string o, string e) = await CliTests.Cli(Env(gd), "render", level, "--out", dir.File("eye.png"), "--camera", "-3,3.5,-4",
            "--look", "-3,3.5,4", "--hfov", "90", "--eye-height", "eye", "--size", "256x128", "--json");
        Assert.True(code == 0, e + o);
        JsonNode cam = JsonNode.Parse(o)!["result"]!["camera"]!;
        Assert.Equal(1.7, cam["position"]![1]!.GetValue<double>(), 3);
        Assert.Equal(1.7, cam["look"]![1]!.GetValue<double>(), 3); // the pitch is kept (level)
        Assert.Equal(0, cam["floor"]!.GetValue<double>(), 3);
        Assert.Equal(53.13, cam["fovDegrees"]!.GetValue<double>(), 1);
        Assert.Equal(90, cam["hFovDegrees"]!.GetValue<double>(), 1);
    }

    [Fact]
    public async Task Cli_Compare_Of_A_Render_With_Itself_Lines_Up_And_Sizes_Are_Deterministic()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using var dir = new TempDir();
        CliEnvironment env = Env(gd);
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        string refs = dir.File("refs");

        // The "concept" is the level itself, rendered from a known camera at 2:1 (wireframe: without an RF install the
        // shaded modes draw each room in one flat colour, which has no edges to compare).
        (int code, string o, string e) = await CliTests.Cli(env, "render", level, "--out", dir.File("shot.png"), "--camera", "-4,1.7,-5",
            "--look", "2,1.2,5", "--hfov", "90", "--size", "320x160", "--mode", "wireframe");
        Assert.True(code == 0, e + o);
        (code, o, e) = await CliTests.Cli(env, "ref", "add", dir.File("shot.png"), "--refs", refs, "--id", "self", "--area", "Room");
        Assert.True(code == 0, e + o);
        (code, o, e) = await CliTests.Cli(env, "ref", "camera", "self", "--refs", refs, "--camera", "-4,1.7,-5", "--look", "2,1.2,5", "--hfov", "90");
        Assert.True(code == 0, e + o);
        Assert.Contains("hfov 90", o, StringComparison.Ordinal);

        (code, o, e) = await CliTests.Cli(env, "ref", "compare", "self", level, "--refs", refs, "--out", dir.File("sheet.png"), "--panel", "320", "--mode", "wireframe", "--json");
        Assert.True(code == 0, e + o);
        JsonNode r = JsonNode.Parse(o)!["result"]!;
        Assert.Equal(ReferenceCommand.SheetWidth(320), r["width"]!.GetValue<int>());
        Assert.Equal(new[] { 320, 160 }, r["panel"]!.AsArray().Select(n => n!.GetValue<int>()));
        (int sw, int sh, _) = PngTestReader.Read(dir.File("sheet.png"));
        Assert.Equal((r["width"]!.GetValue<int>(), r["height"]!.GetValue<int>()), (sw, sh));
        Assert.True(File.Exists(dir.File("sheet.render.png")));
        JsonNode cues = r["cues"]!;
        Assert.True(cues["edgeOverlapReference"]!.GetValue<double>() > 0.9, cues.ToJsonString());
        Assert.True(cues["edgeOverlapRender"]!.GetValue<double>() > 0.9, cues.ToJsonString());
        Assert.Equal(cues["reference"]!["edgeDensity"]!.GetValue<double>(), cues["render"]!["edgeDensity"]!.GetValue<double>(), 2);
        // The camera pitches down by asin(0.5 / |look - eye|); hfov 90 at 2:1 has a focal length of one picture height.
        double horizon = 0.5 + Math.Tan(Math.Asin(-0.5 / Math.Sqrt(36 + 0.25 + 100)));
        Assert.Equal(horizon, cues["render"]!["horizon"]!.GetValue<double>(), 2);
        File.Copy(dir.File("sheet.png"), Path.Combine(ArtifactsDir, "self_compare.png"), overwrite: true);

        // A one-off override moves the render without changing the stored camera.
        (code, o, e) = await CliTests.Cli(env, "ref", "compare", "self", level, "--refs", refs, "--out", dir.File("moved.png"), "--panel", "320",
            "--mode", "wireframe", "--move", "0,2,0", "--json");
        Assert.True(code == 0, e + o);
        Assert.True(JsonNode.Parse(o)!["result"]!["cues"]!["edgeOverlapReference"]!.GetValue<double>() < 0.9);
        Assert.Equal(-4, ReferenceStore.Load(refs).Get("self").Camera!.Position[0], 3);

        // Readable output and usage errors.
        (code, o, _) = await CliTests.Cli(env, "ref", "scale", "2", "door_height");
        Assert.Equal(0, code);
        Assert.Contains("= 5 m", o, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, (await CliTests.Cli(env, "ref")).Code);
        Assert.Equal(ExitCodes.Usage, (await CliTests.Cli(env, "ref", "bogus")).Code);
        Assert.Equal(ExitCodes.Usage, (await CliTests.Cli(env, "ref", "compare", "self", "--refs", refs, "--out", dir.File("x.png"))).Code);
        Assert.Equal(ExitCodes.Usage, (await CliTests.Cli(env, "ref", "list", "--refs", refs, "--view", "top")).Code);
        Assert.Equal(ExitCodes.Usage, (await CliTests.Cli(env, "info", level, "--refs", refs)).Code);
        Assert.Equal(ExitCodes.Usage, (await CliTests.Cli(env, "ref", "camera", "nope", "--refs", refs, "--pitch", "3")).Code);
    }

    [Fact]
    public async Task Mcp_Reference_Tools_Register_Mark_And_Compare_With_An_Image()
    {
        GraphicsDevice? probe = TryDevice();
        if (probe is null)
        {
            return;
        }

        probe.Dispose(); // the session's renders create their own device
        await using McpTestSession s = await McpTestSession.StartAsync();
        string level = s.WriteRoomLevel();
        await s.OkAsync("open_level", new { path = level });
        string concept = WritePanel(s.Dir.File("concept.png"));

        JsonObject added = await s.OkAsync("reference_edit", new { op = "add", image = concept, id = "p", crop = new[] { 37.0, 28, 205, 146 }, snap = true, area = "Panel" });
        Assert.StartsWith(Path.Combine(s.Scratch, "references"), added["refs"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(196, added["reference"]!["width"]!.GetValue<int>());

        (bool isError, JsonObject view, CallToolResult viewResult) = await s.CallAsync("reference_view", new { id = "p" });
        Assert.False(isError, view.ToJsonString());
        Assert.IsType<ImageContentBlock>(viewResult.Content[1]);

        JsonObject cam = await s.OkAsync("reference_edit", new { op = "camera", id = "p", camera = new[] { -4.0, 1.7, -5 }, look = new[] { 2.0, 1.2, 5 }, horizon = new[] { 0.55 }, eye_height = 1.7 });
        Assert.Equal(1.7, cam["camera"]!["eyeHeight"]!.GetValue<double>(), 3);
        Assert.NotNull(cam["estimate"]!["pitchDeg"]);

        (isError, JsonObject cmp, CallToolResult cmpResult) = await s.CallAsync("reference_compare", new { id = "p", panel = 300 });
        Assert.False(isError, cmp.ToJsonString());
        var image = Assert.IsType<ImageContentBlock>(cmpResult.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(ReferenceCommand.SheetWidth(300), cmp["width"]!.GetValue<int>());
        Assert.NotNull(cmp["cues"]!["hints"]);
        Assert.Equal(0, cmp["camera"]!["floor"]!.GetValue<double>(), 3);

        JsonObject listed = await s.OkAsync("reference_view"); // neither image nor id: the list
        Assert.Equal(1, listed["count"]!.GetValue<int>());
        McpResourceText scale = (await s.Service.ReadResourceAsync(McpResources.ReferenceScaleUri, default))!;
        Assert.Contains("door_height", scale.Text, StringComparison.Ordinal);

        // The reference set must lie in a folder this session may write.
        string outside = Path.Combine(Path.GetTempPath(), "ged-not-allowed-" + Guid.NewGuid().ToString("N"));
        await s.FailAsync("reference_edit", AutomationErrorCodes.PathNotAllowed, new { op = "add", image = concept, id = "q", refs_dir = outside });
        Assert.False(Directory.Exists(outside));
    }
}

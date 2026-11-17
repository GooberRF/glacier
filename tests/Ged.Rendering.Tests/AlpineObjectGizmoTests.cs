using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Core.Tables;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// The Alpine Faction 1.5 object glyphs (chunk C5): each object emits its own icon, and the line glyphs Alpine RED
/// draws — weather and directional-light volumes behind the range gate, vehicle-factory and projection-camera facing
/// arrows, the projection frustum, the rope polyline, waypoint-helper spheres and links, and curved / closing mover
/// path segments in place of the straight keyframe link. A GPU pass renders a close-up of every glyph to PNGs under
/// tests/artifacts (and pixel-gates each glyph colour).
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class AlpineObjectGizmoTests
{
    private const int W = 900;
    private const int H = 640;

    private static uint Rgba(byte r, byte g, byte b) => Palette.Rgba(r, g, b);

    /// <summary>A level with one of every Alpine 1.5 object, set up so every glyph is drawn.</summary>
    private static (EditorDocument Doc, Dictionary<LevelObjectKind, int> Uids) Level()
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15));
        var uids = new Dictionary<LevelObjectKind, int>();
        LevelObject Place(LevelObjectKind k, Vec3 p)
        {
            LevelObject o = doc.PlaceObject(k, p)!;
            uids[k] = o.Uid;
            return o;
        }

        var weather = (AlpineWeatherRegion)Place(LevelObjectKind.WeatherRegion, new Vec3(-12, 4, 0)).Model;
        weather.AlwaysShowRange = 1;
        weather.Width = 6;
        weather.Height = 4;
        weather.Depth = 6;

        var factory = (AlpineVehicleFactory)Place(LevelObjectKind.VehicleFactory, new Vec3(-4, 0, 0)).Model;
        factory.VehicleClass = "Stationary Turret_Plain";
        factory.TurretYawLimitDegrees = 60;

        LevelObject cam = Place(LevelObjectKind.ProjectionCamera, new Vec3(4, 3, 0));
        var ev = (RflEvent)doc.PlaceEvent(EventSchemaCatalog.Find("Display_Projection")!, new Vec3(4, 5, 0))!.Model;
        ev.Links.Add(cam.Uid);
        ev.Float1 = 70f;
        ev.Int1 = 512;
        ev.Int2 = 256;

        LevelObject target = doc.PlaceObject(LevelObjectKind.Target, new Vec3(18, 6, 0))!;
        var rope = (AlpineRopeEmitter)Place(LevelObjectKind.RopeEmitter, new Vec3(10, 6, 0)).Model;
        rope.TargetUid = target.Uid;
        rope.Slack = 3f;
        rope.Color = new RfColor(255, 60, 60, 255);

        var light = (AlpineDirectionalLight)Place(LevelObjectKind.DirectionalLight, new Vec3(0, 10, 10)).Model;
        light.Shape = 1;
        light.AlwaysShowRange = 1;
        light.ExtentX = 6;
        light.ExtentY = 4;
        light.ExtentZ = 3;
        light.BoxYaw = 30;

        var h1 = (AlpineWaypointHelper)Place(LevelObjectKind.WaypointHelper, new Vec3(-6, 0, 12)).Model;
        var h2 = (AlpineWaypointHelper)doc.PlaceObject(LevelObjectKind.WaypointHelper, new Vec3(6, 0, 12))!.Model;
        h1.Radius = 2f;
        h2.Radius = 2f;
        h1.Flags = 1;
        h1.Links.Add(h2.Uid);
        return (doc, uids);
    }

    private static RenderScene Build(EditorDocument doc, SceneBuildOptions? options = null) =>
        SceneBuilder.Build(doc.Rfl, options ?? new SceneBuildOptions { IncludeStaticGeometry = false, BillboardSize = 0.6f });

    [Theory]
    [InlineData(LevelObjectKind.WeatherRegion, BillboardKind.WeatherRegion, (int)EditorIcon.WeatherRegion)]
    [InlineData(LevelObjectKind.VehicleFactory, BillboardKind.VehicleFactory, (int)EditorIcon.VehicleFactory)]
    [InlineData(LevelObjectKind.ProjectionCamera, BillboardKind.ProjectionCamera, (int)EditorIcon.ProjectionCamera)]
    [InlineData(LevelObjectKind.RopeEmitter, BillboardKind.RopeEmitter, (int)EditorIcon.RopeEmitter)]
    [InlineData(LevelObjectKind.DirectionalLight, BillboardKind.DirectionalLight, (int)EditorIcon.DirectionalLight)]
    [InlineData(LevelObjectKind.WaypointHelper, BillboardKind.WaypointHelper, (int)EditorIcon.WaypointHelper)]
    public void Each_Alpine_Object_Emits_Its_Own_Icon(LevelObjectKind kind, BillboardKind billboard, int icon)
    {
        (EditorDocument doc, Dictionary<LevelObjectKind, int> uids) = Level();
        RenderScene scene = Build(doc);
        Billboard bb = Assert.Single(scene.Billboards, b => b.Kind == billboard && b.PickId.Index == uids[kind]);
        Assert.Equal(icon, bb.Icon);
    }

    [Fact]
    public void Volumes_Follow_The_Range_Gate()
    {
        (EditorDocument doc, _) = Level();
        uint rain = Rgba(64, 160, 255);
        uint lightOn = Rgba(255, 224, 96);
        RenderScene shown = Build(doc);
        Assert.Equal(12, shown.Lines.Count(l => l.Color == rain)); // a box: 12 edges

        foreach (LevelObject o in doc.Objects)
        {
            switch (o.Model)
            {
                case AlpineWeatherRegion w:
                    w.AlwaysShowRange = 0;
                    break;
                case AlpineDirectionalLight l:
                    l.AlwaysShowRange = 0;
                    break;
            }
        }

        RenderScene hidden = Build(doc);
        Assert.Equal(0, hidden.Lines.Count(l => l.Color == rain));
        Assert.True(shown.Lines.Count(l => l.Color == lightOn) > hidden.Lines.Count(l => l.Color == lightOn),
            "the directional light's box is drawn only behind the range gate; its travel arrow always");
        Assert.True(hidden.Lines.Any(l => l.Color == lightOn), "the travel arrow stays");
    }

    [Fact]
    public void A_Box_Directional_Light_Is_World_Aligned_After_Box_Yaw()
    {
        var lines = new List<LineSegment>();
        var light = new AlpineDirectionalLight { Position = Vec3.Zero, Shape = 1, ExtentX = 2, ExtentY = 2, ExtentZ = 2, BoxYaw = 90 };
        AlpineObjectGizmos.AddDirectionalLightVolume(lines, light);
        // Yaw 90 turns local +Z onto world +X (alpine_dir_light.h:216-224): corners stay on the unit cube, Y unchanged.
        Assert.All(lines, l => Assert.True(MathF.Abs(MathF.Abs(l.A.Y) - 1f) < 1e-4f && MathF.Abs(MathF.Abs(l.A.X) - 1f) < 1e-4f));
    }

    [Fact]
    public void The_Projection_Frustum_Uses_The_Linked_Display_Projection_Fov_And_Aspect()
    {
        (EditorDocument doc, Dictionary<LevelObjectKind, int> uids) = Level();
        (float fov, float aspect) = AlpineObjectGizmos.ProjectionView(uids[LevelObjectKind.ProjectionCamera], doc.Rfl);
        Assert.Equal(70f, fov);
        Assert.Equal(2f, aspect);
        Assert.Equal((50f, 1f), AlpineObjectGizmos.ProjectionView(987654, doc.Rfl)); // no event: the game's defaults
    }

    [Fact]
    public void A_Rope_Hangs_To_Its_Target_And_A_Waypoint_Link_Is_Drawn()
    {
        (EditorDocument doc, _) = Level();
        RenderScene scene = Build(doc);
        uint ropeTint = Rgba(153, 64, 64); // 255,60,60 dimmed to 3/5, floored at 0x40
        List<LineSegment> rope = scene.Lines.Where(l => l.Color == ropeTint).ToList();
        Assert.Equal(24, rope.Count);
        Assert.Equal(new Vector3(10, 6, 0), rope[0].A);
        Assert.True(Vector3.Distance(new Vector3(18, 6, 0), rope[^1].B) < 1e-3f);
        Assert.True(rope.Min(l => l.A.Y) < 5f, "a slack rope sags");

        // The helper link (link colour) from h1 to h2 and the landing arrow (yellow).
        Assert.Contains(scene.Lines, l => l.A == new Vector3(-6, 0, 12) && l.B == new Vector3(6, 0, 12));
        Assert.Contains(scene.Lines, l => l.Color == Rgba(255, 255, 0) && l.A == new Vector3(-6, 0, 12) && l.B.Y < 0f);
    }

    [Fact]
    public void A_Turret_Factory_Draws_Its_Two_Yaw_Stops()
    {
        (EditorDocument doc, _) = Level();
        RenderScene scene = Build(doc);
        uint yellow = Rgba(255, 255, 0);
        // Two stop arrows from the factory at (-4, 0, 0): shaft + 4 head wings each.
        Assert.Equal(10, scene.Lines.Count(l => l.Color == yellow && (l.A == new Vector3(-4, 0, 0) || Vector3.Distance(l.A, new Vector3(-4, 0, 0)) < 1.6f)));
    }

    [Fact]
    public void Curved_Mover_Segments_Replace_The_Straight_Keyframe_Link()
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15));
        int box = new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, Vec3.Zero, Mat3.Identity);
        var movers = new MoverService(doc);
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "cart");
        Keyframe k1 = movers.AddKeyframe(g, new Vec3(10, 0, 0), Mat3.Identity);
        movers.AddKeyframe(g, new Vec3(10, 0, 10), Mat3.Identity);
        g.MovingData!.MovementType = 5; // loop: a closing segment

        RenderScene straight = Build(doc);
        uint link = Palette.Rgba(255, 40, 40, 220);
        int straightCount = straight.Lines.Count(l => l.Color == link);

        InspectorField segment = MoverInspectorSchema.KeyframePathFields.Single(f => f.EffectiveKey == "path_segment");
        InspectorAccess.Set(doc, doc.FindByUid(k1.Uid)!, segment, 1);
        RenderScene curved = Build(doc);
        List<LineSegment> curve = curved.Lines.Where(l => l.Color == link).ToList();
        Assert.True(curve.Count > straightCount + 20, "a 24-step curve replaces one straight link");

        // The curve leaves the chord between the two keyframes (auto tangents bend it).
        Assert.Contains(curve, l => l.B.X > 10.05f);
    }

    // ---- GPU close-ups ----------------------------------------------------------------

    [Fact]
    public void Closeups_Of_Every_Alpine_Glyph()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        gd.SetIconAtlas(IconAtlas.Build());
        (EditorDocument doc, Dictionary<LevelObjectKind, int> uids) = Level();
        var options = new SceneBuildOptions { IncludeStaticGeometry = false, BillboardSize = 0.6f, ShowAllRanges = true };
        RenderScene scene = Build(doc, options);

        var shots = new (string Name, Vec3 Center, float Half, uint Color)[]
        {
            ("alpine_weather_region", new Vec3(-12, 4, 0), 3.5f, Rgba(64, 160, 255)),
            ("alpine_vehicle_factory", new Vec3(-4, 0, 0), 1.6f, Rgba(255, 255, 0)),
            ("alpine_projection_camera", new Vec3(5, 3, 0), 1.8f, Rgba(255, 192, 32)),
            ("alpine_rope_emitter", new Vec3(14, 4, 0), 3.5f, Rgba(153, 64, 64)),
            ("alpine_directional_light", new Vec3(0, 10, 10), 3.5f, Rgba(255, 224, 96)),
            ("alpine_waypoint_helpers", new Vec3(0, 0, 12), 5f, Rgba(60, 220, 60)),
        };

        var summary = new List<string>();
        foreach ((string name, Vec3 c, float half, uint color) in shots)
        {
            var camera = new Camera { Projection = CameraProjection.Perspective };
            camera.Frame(new Aabb(new Vec3(c.X - half, c.Y - half, c.Z - half), new Vec3(c.X + half, c.Y + half, c.Z + half)));
            byte[] pixels = OffscreenRenderer.Render(gd, scene, null, camera, RenderMode.Wireframe, W, H);
            File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, name + ".png"), PngWriter.Encode(W, H, pixels));
            int hits = CountColor(pixels, color, 40);
            summary.Add($"{name}: {hits} glyph-colour pixels");
            Assert.True(hits > 30, $"{name}: expected the glyph colour on screen, found {hits} pixels");
        }

        File.WriteAllLines(Path.Combine(RenderTestSupport.ArtifactsDir, "alpine_glyphs_summary.txt"), summary);
        _ = uids;
    }

    /// <summary>Pixels within <paramref name="tolerance"/> (per channel) of a packed R8G8B8A8 colour.</summary>
    private static int CountColor(byte[] rgba, uint color, int tolerance)
    {
        int r = (int)(color & 0xFF), g = (int)((color >> 8) & 0xFF), b = (int)((color >> 16) & 0xFF);
        int n = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (Math.Abs(rgba[i] - r) <= tolerance && Math.Abs(rgba[i + 1] - g) <= tolerance && Math.Abs(rgba[i + 2] - b) <= tolerance)
            {
                n++;
            }
        }

        return n;
    }
}

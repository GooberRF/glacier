using System.Numerics;
using Ged.App.Camera;
using Ged.Core.Input;
using Ged.Rendering;
using Xunit;
using Cam = Ged.Rendering.Camera;

namespace Ged.App.Tests;

/// <summary>
/// Item 2 regression coverage: the stock ortho camera controls (red-stock-inventory §3)
/// must work in orthographic panes under EVERY camera scheme — Shift+LMB / Shift+RMB
/// slide, Shift+both drag-zoom, A/Z zoom, main-row +/- coarse zoom, cursor-centred
/// wheel zoom, and numpad/arrow view slides. Previously most of these routed to
/// perspective-only camera ops (Rotate / forward dolly) that are no-ops in ortho.
/// </summary>
public sealed class OrthoSchemeTests
{
    public static TheoryData<CameraSchemeKind> AllSchemes => new()
    {
        CameraSchemeKind.RedClassic,
        CameraSchemeKind.ModernFps,
        CameraSchemeKind.Orbit,
        CameraSchemeKind.UnrealEd,
    };

    private static Cam TopCam() => new()
    {
        Projection = CameraProjection.Orthographic,
        Ortho = OrthoView.Top,
        Position = Vector3.Zero,
        OrthoZoom = 10f,
        AspectRatio = 800f / 600f,
    };

    private static ViewportInputState OrthoState() => new()
    {
        Ortho = true,
        ViewWidth = 800f,
        ViewHeight = 600f,
        PointerX = 400f,
        PointerY = 300f,
    };

    // ---- Shift+mouse slides -------------------------------------------------

    [Theory]
    [MemberData(nameof(AllSchemes))]
    public void Shift_LMB_Drag_Slides_The_View_Plane(CameraSchemeKind kind)
    {
        ICameraScheme scheme = CameraSchemes.Create(kind);
        Cam cam = TopCam();
        ViewportInputState s = OrthoState();
        s.Modifiers = GestureModifiers.Shift;
        s.LeftDown = true;

        scheme.Drag(cam, 10f, -6f, s);

        Assert.True(cam.Position.X < 0f, $"{kind}: dragging right must slide the view left");
        Assert.True(cam.Position.Z < 0f, $"{kind}: dragging up must slide the view down");
        Assert.Equal(0f, cam.Position.Y, 3); // never along the invisible depth axis
    }

    [Theory]
    [MemberData(nameof(AllSchemes))]
    public void Shift_RMB_Drag_Slides_The_View_Plane(CameraSchemeKind kind)
    {
        ICameraScheme scheme = CameraSchemes.Create(kind);
        Cam cam = TopCam();
        ViewportInputState s = OrthoState();
        s.Modifiers = GestureModifiers.Shift;
        s.RightDown = true;

        scheme.Drag(cam, 10f, 0f, s);

        Assert.True(cam.Position.X < 0f, $"{kind}: Shift+RMB must slide in ortho, not freelook");
        Assert.Equal(0f, cam.Position.Y, 3);
        Assert.Equal(0f, cam.Yaw, 3); // no rotation leaked through
    }

    [Theory]
    [MemberData(nameof(AllSchemes))]
    public void Shift_Both_Buttons_Drag_Zooms(CameraSchemeKind kind)
    {
        ICameraScheme scheme = CameraSchemes.Create(kind);
        Cam cam = TopCam();
        ViewportInputState s = OrthoState();
        s.Modifiers = GestureModifiers.Shift;
        s.LeftDown = true;
        s.RightDown = true;

        scheme.Drag(cam, 0f, 20f, s);
        Assert.True(cam.OrthoZoom > 10f, $"{kind}: dragging down with both buttons must zoom out");
    }

    // ---- Key zooms ----------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllSchemes))]
    public void A_And_Z_Scale_OrthoZoom(CameraSchemeKind kind)
    {
        ICameraScheme scheme = CameraSchemes.Create(kind);
        Cam cam = TopCam();
        ViewportInputState s = OrthoState();

        s.HeldKeys.Add("A");
        scheme.Move(cam, 0.1f, s);
        Assert.True(cam.OrthoZoom < 10f, $"{kind}: A must zoom in (shrink OrthoZoom)");

        float afterA = cam.OrthoZoom;
        s.HeldKeys.Clear();
        s.HeldKeys.Add("Z");
        scheme.Move(cam, 0.1f, s);
        Assert.True(cam.OrthoZoom > afterA, $"{kind}: Z must zoom out (grow OrthoZoom)");
        Assert.Equal(Vector3.Zero, cam.Position); // zoom keys never pan
    }

    [Theory]
    [MemberData(nameof(AllSchemes))]
    public void Plus_And_Minus_Are_Coarse_Zoom(CameraSchemeKind kind)
    {
        ICameraScheme scheme = CameraSchemes.Create(kind);
        ViewportInputState s = OrthoState();

        Cam fine = TopCam();
        s.HeldKeys.Add("A");
        scheme.Move(fine, 0.1f, s);

        Cam coarse = TopCam();
        s.HeldKeys.Clear();
        s.HeldKeys.Add("Plus");
        scheme.Move(coarse, 0.1f, s);

        Assert.True(coarse.OrthoZoom < fine.OrthoZoom,
            $"{kind}: +/- must zoom faster than A/Z (coarse vs fine)");

        Cam outward = TopCam();
        s.HeldKeys.Clear();
        s.HeldKeys.Add("Minus");
        scheme.Move(outward, 0.1f, s);
        Assert.True(outward.OrthoZoom > 10f, $"{kind}: Minus must zoom out");
    }

    // ---- Wheel --------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllSchemes))]
    public void Wheel_Zooms_Cursor_Centred(CameraSchemeKind kind)
    {
        ICameraScheme scheme = CameraSchemes.Create(kind);
        Cam cam = TopCam();
        cam.Position = new Vector3(4f, 0f, -3f);
        ViewportInputState s = OrthoState();
        s.PointerX = 600f;
        s.PointerY = 150f;

        Vector3 anchor = cam.PixelRay(600f, 150f, 800f, 600f).Origin;
        scheme.Wheel(cam, 120, s);

        Assert.Equal(8.5f, cam.OrthoZoom, 3);
        Vector3 after = cam.PixelRay(600f, 150f, 800f, 600f).Origin;
        Assert.True(Vector3.Distance(anchor, after) < 1e-3f,
            $"{kind}: the world point under the cursor must stay put while wheel-zooming");

        scheme.Wheel(cam, -120, s);
        Assert.Equal(10f, cam.OrthoZoom, 3);
    }

    // ---- Numpad / arrow slides ----------------------------------------------

    [Theory]
    [MemberData(nameof(AllSchemes))]
    public void Numpad_And_Arrow_Keys_Slide_The_View(CameraSchemeKind kind)
    {
        ICameraScheme scheme = CameraSchemes.Create(kind);
        ViewportInputState s = OrthoState();

        foreach ((string token, float x, float z) in new[]
        {
            ("Numpad3", 1f, 0f),    // stock slide right
            ("Numpad1", -1f, 0f),   // stock slide left
            ("NumpadPlus", 0f, 1f),   // stock up = view-plane up
            ("NumpadEnter", 0f, -1f), // stock down (NumpadEnter — distinct from main Enter)
            ("Numpad6", 1f, 0f),    // numpad arrows slide in ortho
            ("Numpad4", -1f, 0f),
            ("Numpad8", 0f, 1f),
            ("Numpad2", 0f, -1f),
            ("Right", 1f, 0f),      // same physical keys without NumLock
            ("Left", -1f, 0f),
            ("Up", 0f, 1f),
            ("Down", 0f, -1f),
        })
        {
            Cam cam = TopCam();
            s.HeldKeys.Clear();
            s.HeldKeys.Add(token);
            scheme.Move(cam, 0.1f, s);

            Assert.True(MathF.Sign(cam.Position.X) == MathF.Sign(x) && MathF.Sign(cam.Position.Z) == MathF.Sign(z),
                $"{kind}: held '{token}' moved to {cam.Position}, expected signs ({x},{z}) in the XZ view plane");
            Assert.Equal(0f, cam.Position.Y, 3);
        }
    }

    // ---- Perspective must be unchanged (RED Classic) ------------------------

    [Fact]
    public void RedClassic_Perspective_ShiftRmb_Still_Freelooks()
    {
        ICameraScheme scheme = CameraSchemes.Create(CameraSchemeKind.RedClassic);
        var cam = new Cam { Projection = CameraProjection.Perspective, Position = Vector3.Zero };
        var s = new ViewportInputState { Modifiers = GestureModifiers.Shift, RightDown = true };

        scheme.Drag(cam, 10f, 0f, s);
        Assert.True(cam.Yaw > 0f, "perspective Shift+RMB must still rotate");
        Assert.Equal(Vector3.Zero, cam.Position);
    }

    [Fact]
    public void RedClassic_Perspective_A_Still_Dollies()
    {
        ICameraScheme scheme = CameraSchemes.Create(CameraSchemeKind.RedClassic);
        var cam = new Cam { Projection = CameraProjection.Perspective, Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
        var s = new ViewportInputState();
        s.HeldKeys.Add("A");

        scheme.Move(cam, 0.1f, s);
        Assert.True(cam.Position.Z > 0f, "perspective A must dolly forward, not zoom");
    }

    // ---- Item 2: RED Classic drops WASD fly; A = zoom-in only; Shift boosts speed ----

    [Theory]
    [InlineData("W")]
    [InlineData("S")]
    [InlineData("D")]
    public void RedClassic_Perspective_WSD_Do_Not_Move_The_Camera(string token)
    {
        // WASD fly is a Modern FPS feature; RED Classic must not bind W/S/D to camera
        // movement (they keep their stock command meanings, e.g. W = Hide All Objects).
        ICameraScheme scheme = CameraSchemes.Create(CameraSchemeKind.RedClassic);
        var cam = new Cam { Projection = CameraProjection.Perspective, Position = Vector3.Zero };
        var s = new ViewportInputState { RightDown = true }; // RMB held: the old WASD-fly trigger
        s.HeldKeys.Add(token);

        scheme.Move(cam, 0.1f, s);

        Assert.Equal(Vector3.Zero, cam.Position);
    }

    [Fact]
    public void RedClassic_A_Still_Zooms_In_While_Rmb_Held()
    {
        // 'A' must stay zoom-in (dolly forward) even with RMB held — it must not be
        // reinterpreted as a WASD strafe.
        ICameraScheme scheme = CameraSchemes.Create(CameraSchemeKind.RedClassic);
        var cam = new Cam { Projection = CameraProjection.Perspective, Position = Vector3.Zero };
        var s = new ViewportInputState { RightDown = true };
        s.HeldKeys.Add("A");

        scheme.Move(cam, 0.1f, s);

        Assert.True(cam.Position.Z > 0f, "A must dolly forward (zoom-in)");
        Assert.Equal(0f, cam.Position.X, 3); // never strafes sideways like WASD's A
    }

    [Fact]
    public void RedClassic_Shift_Multiplies_Movement_Speed()
    {
        ICameraScheme scheme = CameraSchemes.Create(CameraSchemeKind.RedClassic);

        var plain = new Cam { Projection = CameraProjection.Perspective, Position = Vector3.Zero };
        var s1 = new ViewportInputState { Speed = 12f };
        s1.HeldKeys.Add("Numpad3"); // slide right
        scheme.Move(plain, 0.1f, s1);

        var boosted = new Cam { Projection = CameraProjection.Perspective, Position = Vector3.Zero };
        var s2 = new ViewportInputState { Speed = 12f, Modifiers = GestureModifiers.Shift };
        s2.HeldKeys.Add("Numpad3");
        scheme.Move(boosted, 0.1f, s2);

        float plainDist = plain.Position.Length();
        float boostedDist = boosted.Position.Length();
        Assert.True(plainDist > 0f, "plain movement should occur");
        Assert.Equal(3f, boostedDist / plainDist, 2); // Shift = x3 boost, mirroring Modern FPS
    }

    // ---- Command-dispatch suppression for scheme-owned keys ------------------

    [Fact]
    public void Schemes_Consume_Their_Continuous_Navigation_Keys()
    {
        Cam ortho = TopCam();
        var perspective = new Cam { Projection = CameraProjection.Perspective };

        ICameraScheme red = CameraSchemes.Create(CameraSchemeKind.RedClassic);
        Assert.True(red.ConsumesKey(ortho, "A"));
        Assert.True(red.ConsumesKey(ortho, "Plus"));
        Assert.True(red.ConsumesKey(ortho, "Numpad1"));
        Assert.True(red.ConsumesKey(perspective, "A"));       // perspective dolly
        Assert.False(red.ConsumesKey(perspective, "Plus"));   // no perspective +/- op
        Assert.False(red.ConsumesKey(perspective, "H"));      // hotkeys still dispatch

        ICameraScheme modern = CameraSchemes.Create(CameraSchemeKind.ModernFps);
        Assert.True(modern.ConsumesKey(ortho, "A"));          // stock ortho controls everywhere
        Assert.False(modern.ConsumesKey(perspective, "A"));   // Modern flies with RMB+WASD only
    }
}

using System.Numerics;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The PLACEMENT ray traces what the user SEES: when compiled static geometry exists it raycasts the
/// COMPILED result only (a raw unmerged / carved-away brush face must not catch the ray where the
/// merged geometry has an opening); on a never-built level it falls back to the visible authored brush
/// faces. The TEXTURE ray is unchanged (brush faces only — editing identity).
/// </summary>
public sealed class PlacementTraceTests
{
    /// <summary>An air room whose walls compile to real surfaces at ±12 (x/z) and ±4 (y).</summary>
    private static void AddAirRoom(EditorSession session)
    {
        int uid = session.Document!.AllocateUid();
        session.BrushEditor!.AddBrush(
            new Brush
            {
                Uid = uid,
                Position = default,
                Rotation = Mat3.Identity,
                Geometry = BrushFactory.Box(24f, 8f, 24f, 0, 0, 0, BrushCreateParams.DefaultTexture),
                Flags = (uint)BrushFlags.Air,
                Life = -1,
                State = BrushState.Normal,
            },
            "Add air room");
    }

    /// <summary>A built room with a RAW solid box added AFTER the build (so it is NOT in the compiled result).</summary>
    private static EditorSession BuiltRoomWithRawBoxInFront(out int rawBoxUid)
    {
        var session = new EditorSession();
        session.NewLevel();
        AddAirRoom(session);
        GeometryBuildService.BuildAndApply(session.Document!.Rfl, new CompileOptions { BuildSurfaces = true });
        session.BuildScene(); // _staticGeometry now holds the compiled walls

        // A raw solid box between the room centre and the far +Z wall (front face ≈ z=5), never rebuilt.
        rawBoxUid = session.BrushEditor!.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(0, 0, 6), Mat3.Identity);
        return session;
    }

    [AvaloniaFact]
    public void Placement_Traces_Compiled_Geometry_Through_A_Raw_Brush_When_Built()
    {
        EditorSession session = BuiltRoomWithRawBoxInFront(out _);

        // Ray from the room centre toward +Z: the raw box front is at z≈5, the compiled wall at z≈12.
        EditorSession.RayFaceHitResult hit = session.RayPlacementHit(new Vector3(0, 0, 0), new Vector3(0, 0, 1), out bool usedCompiled);

        Assert.True(usedCompiled, "compiled geometry exists → the placement query must be compiled-only");
        Assert.True(hit.Hit);
        Assert.Equal(-1, hit.BrushUid);            // a compiled face, never the raw brush
        Assert.True(hit.Point.Z > 6f, $"expected the far compiled wall (z≈12), got the raw brush at z={hit.Point.Z}");
    }

    [AvaloniaFact]
    public void Placement_Falls_Back_To_Authored_Brush_Faces_On_A_Never_Built_Level()
    {
        var session = new EditorSession();
        session.NewLevel();
        int uid = session.BrushEditor!.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 4, Height = 4, Depth = 4 }, new Vec3(0, 0, 0), Mat3.Identity);

        EditorSession.RayFaceHitResult hit = session.RayPlacementHit(new Vector3(0, 0, 20), new Vector3(0, 0, -1), out bool usedCompiled);

        Assert.False(usedCompiled, "no compiled geometry → fall back to the visible authored brush faces");
        Assert.True(hit.Hit);
        Assert.Equal(uid, hit.BrushUid);
    }

    [AvaloniaFact]
    public void Texture_Ray_Still_Resolves_The_Raw_Brush_Even_With_Compiled_Geometry()
    {
        EditorSession session = BuiltRoomWithRawBoxInFront(out int rawBoxUid);

        // Same scene, texture resolver: it hits the RAW box in front (z≈5), not the compiled wall behind
        // — editing identity is on the authored brush the user is texturing.
        EditorSession.BrushFaceHit hit = session.RayBrushFaceHit(new Vector3(0, 0, 0), new Vector3(0, 0, 1));

        Assert.True(hit.Hit);
        Assert.Equal(rawBoxUid, hit.BrushUid);
        Assert.True(hit.Point.Z < 6f, $"texture must resolve the near raw brush (z≈5), got z={hit.Point.Z}");
    }
}

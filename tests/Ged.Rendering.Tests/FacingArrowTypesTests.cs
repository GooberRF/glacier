using System;
using System.Linq;
using System.Numerics;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Facing arrows for more directional object types (MP respawn points, the Player Start and
/// directional coronas) share the exact machinery and the single "Show Event Arrows" gate the
/// directional-event arrows use. An omnidirectional corona (full 360° visibility cone) has no
/// meaningful facing and is skipped.
/// </summary>
public sealed class FacingArrowTypesTests
{
    private static bool StartsAt(RenderScene scene, Vector3 p) =>
        scene.Lines.Any(l => Vector3.Distance(l.A, p) < 1e-3f);

    /// <summary>
    /// The unit direction of the facing-arrow shaft emitted at <paramref name="p"/>. The shaft
    /// is the single segment whose start point is the object position (arrow-head wings start
    /// 85% of the way along, never at the base), so it uniquely identifies the drawn direction.
    /// </summary>
    private static Vector3 ShaftDir(RenderScene scene, Vector3 p)
    {
        LineSegment shaft = scene.Lines.Single(l => Vector3.Distance(l.A, p) < 1e-3f);
        return Vector3.Normalize(shaft.B - shaft.A);
    }

    [Fact]
    public void Respawn_Point_Emits_A_Facing_Arrow_Gated_By_The_Toggle()
    {
        var respawns = new MpRespawnPointsSection();
        respawns.Points.Add(new MpRespawnPoint { Uid = 1, Position = new Vec3(5, 0, 0), Rotation = Mat3.Identity });
        RflFile file = Wrap(SectionType.MpRespawnPoints, respawns);

        RenderScene on = SceneBuilder.Build(file, new SceneBuildOptions());
        Assert.True(StartsAt(on, new Vector3(5, 0, 0)));
        Assert.Contains(on.Lines, l => Vector3.Distance(l.A, new Vector3(5, 0, 0)) < 1e-3f && l.B.Z > 0f); // shaft runs +Z (identity forward)

        RenderScene off = SceneBuilder.Build(file, new SceneBuildOptions { EventFacingArrows = false });
        Assert.False(StartsAt(off, new Vector3(5, 0, 0)));
    }

    [Fact]
    public void Player_Start_Emits_A_Facing_Arrow_Gated_By_The_Toggle()
    {
        var start = new PlayerStartSection { Position = new Vec3(2, 3, 4), Rotation = Mat3.Identity };
        RflFile file = Wrap(SectionType.PlayerStart, start);

        RenderScene on = SceneBuilder.Build(file, new SceneBuildOptions());
        Assert.True(StartsAt(on, new Vector3(2, 3, 4)));

        RenderScene off = SceneBuilder.Build(file, new SceneBuildOptions { EventFacingArrows = false });
        Assert.False(StartsAt(off, new Vector3(2, 3, 4)));
    }

    [Fact]
    public void Directional_Corona_Is_Arrowed_But_An_Omnidirectional_One_Is_Not()
    {
        var coronas = new AlpineCoronaObjectsSection();
        coronas.Coronas.Add(new AlpineCoronaObject { Uid = 1, Position = new Vec3(1, 0, 0), Orientation = Mat3.Identity, ConeAngle = 45f });   // spotlight
        coronas.Coronas.Add(new AlpineCoronaObject { Uid = 2, Position = new Vec3(0, 1, 0), Orientation = Mat3.Identity, ConeAngle = 360f });  // all-angle = omni
        RflFile file = Wrap(SectionType.AlpineCoronaObjects, coronas);

        RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());

        Assert.True(StartsAt(scene, new Vector3(1, 0, 0)));  // directional corona: arrowed
        Assert.False(StartsAt(scene, new Vector3(0, 1, 0))); // omnidirectional corona: no arrow
    }

    [Fact]
    public void Directional_Corona_Arrow_Points_Along_The_Orientation_Up_Row_Not_Forward()
    {
        // Real ceiling corona from geddmabruptdecayrc2a27.rfl (uid 11187): the cone aims
        // straight DOWN (Up row = (0,-1,0)); the forward/right rows carry the sprite's in-plane
        // spin (a ~51° yaw), so an arrow drawn along forward would read sideways.
        var orient = new Mat3(
            new Vec3(0.63f, 0f, -0.78f),   // forward: in-plane spin, NOT the aim
            new Vec3(0.78f, 0f, 0.63f),    // right:   in-plane spin
            new Vec3(0f, -1f, 0f));        // up:      the true cone/aim direction
        var coronas = new AlpineCoronaObjectsSection();
        coronas.Coronas.Add(new AlpineCoronaObject { Uid = 1, Position = new Vec3(1, 0, 0), Orientation = orient, ConeAngle = 170f });
        RflFile file = Wrap(SectionType.AlpineCoronaObjects, coronas);

        RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());

        Vector3 dir = ShaftDir(scene, new Vector3(1, 0, 0));
        Assert.True(Vector3.Dot(dir, new Vector3(0, -1, 0)) > 0.99f, $"arrow should aim down (Up row); got {dir}");
        // Regression guard: it must NOT follow the forward row (the old, sideways behaviour).
        Assert.True(MathF.Abs(Vector3.Dot(dir, new Vector3(0.63f, 0f, -0.78f))) < 0.05f, $"arrow must not follow forward; got {dir}");
    }

    [Fact]
    public void Directional_Corona_Arrow_Follows_A_Non_Axis_Aligned_Cone_Aim()
    {
        // A corona whose cone aims along an oblique world direction: the Up row is the only row
        // the arrow may read, so a diagonal aim must come through exactly.
        Vector3 aim = Vector3.Normalize(new Vector3(1f, 2f, 2f)); // (0.333, 0.667, 0.667)
        var orient = new Mat3(
            new Vec3(1f, 0f, 0f),          // forward/right: arbitrary, must be ignored
            new Vec3(0f, 0f, 1f),
            new Vec3(aim.X, aim.Y, aim.Z)); // up: the oblique cone direction
        var coronas = new AlpineCoronaObjectsSection();
        coronas.Coronas.Add(new AlpineCoronaObject { Uid = 2, Position = new Vec3(4, 5, 6), Orientation = orient, ConeAngle = 90f });
        RflFile file = Wrap(SectionType.AlpineCoronaObjects, coronas);

        RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());

        Vector3 dir = ShaftDir(scene, new Vector3(4, 5, 6));
        Assert.True(Vector3.Dot(dir, aim) > 0.99f, $"arrow should follow the oblique Up aim {aim}; got {dir}");
    }

    [Fact]
    public void Corona_IsDirectional_Rule_Treats_360_As_Omnidirectional()
    {
        Assert.True(new AlpineCoronaObject { ConeAngle = 1f }.IsDirectional);
        Assert.True(new AlpineCoronaObject { ConeAngle = 359.9f }.IsDirectional);
        Assert.False(new AlpineCoronaObject { ConeAngle = 360f }.IsDirectional);   // effects.tbl: 360 = all-angle visibility
        Assert.False(new AlpineCoronaObject { ConeAngle = 400f }.IsDirectional);
        Assert.False(new AlpineCoronaObject { ConeAngle = 0f }.IsDirectional);     // degenerate / unset
        Assert.False(new AlpineCoronaObject { ConeAngle = -5f }.IsDirectional);
    }

    [Fact]
    public void Corona_Toggle_Off_Suppresses_The_Arrow()
    {
        var coronas = new AlpineCoronaObjectsSection();
        coronas.Coronas.Add(new AlpineCoronaObject { Uid = 1, Position = new Vec3(1, 0, 0), Orientation = Mat3.Identity, ConeAngle = 45f });
        RflFile file = Wrap(SectionType.AlpineCoronaObjects, coronas);

        RenderScene off = SceneBuilder.Build(file, new SceneBuildOptions { EventFacingArrows = false });
        Assert.False(StartsAt(off, new Vector3(1, 0, 0)));
    }

    private static RflFile Wrap(SectionType type, IRflSectionContent content)
    {
        var rfl = new RflFile();
        rfl.Sections.Add(new RflSection((uint)type, Array.Empty<byte>()) { Content = content, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }
}

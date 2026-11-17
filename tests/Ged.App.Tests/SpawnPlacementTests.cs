using System.Numerics;
using Ged.App;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// GUI placement of the floor-standing kinds (<see cref="Ged.Core.Editing.SpawnClearance"/>): Place at Cursor and a
/// palette drag-drop onto a floor stand MP respawn points, the Player Start and waypoint helpers 1 m up along the
/// floor normal; other kinds, wall hits and drops in open space are unchanged.
/// </summary>
public sealed class SpawnPlacementTests
{
    private static readonly Vec3 Floor = new(0, 1, 0);

    [Theory]
    [InlineData(LevelObjectKind.MpRespawnPoint)]
    [InlineData(LevelObjectKind.WaypointHelper)]
    [InlineData(LevelObjectKind.PlayerStart)]
    public void Place_At_Cursor_Lifts_Floor_Standing_Kinds_A_Metre_Off_A_Floor(LevelObjectKind kind)
    {
        Assert.Equal(new Vec3(4, 3, -2), MainWindow.PlaceAtCursorPoint(kind, new Vec3(4, 2, -2), Floor));
        Assert.Equal(new Vec3(4, 2, -2), MainWindow.PlaceAtCursorPoint(kind, new Vec3(4, 2, -2), new Vec3(0, 0, -1))); // wall
    }

    [Theory]
    [InlineData(LevelObjectKind.Light)]
    [InlineData(LevelObjectKind.Item)]
    [InlineData(LevelObjectKind.Trigger)]
    public void Place_At_Cursor_Leaves_Other_Kinds_On_The_Hit(LevelObjectKind kind) =>
        Assert.Equal(new Vec3(4, 2, -2), MainWindow.PlaceAtCursorPoint(kind, new Vec3(4, 2, -2), Floor));

    [Fact]
    public void A_Palette_Drop_On_A_Floor_Stands_The_Spawn_A_Metre_Up_Along_The_Normal()
    {
        Assert.Equal(new Vector3(0, 1, 0), MainWindow.DropOffset(PlaceableKind.Class, nameof(LevelObjectKind.MpRespawnPoint), true, new Vec3(3, 0, 3), Floor));
        Vector3 slope = MainWindow.DropOffset(PlaceableKind.Class, nameof(LevelObjectKind.WaypointHelper), true, Vec3.Zero, new Vec3(0, 0.8f, 0.6f))!.Value;
        Assert.Equal(0.8f, slope.Y, 4);
        Assert.Equal(0.6f, slope.Z, 4);

        // Not applicable: another kind, a wall, open space (no surface), a mesh drop.
        Assert.Null(MainWindow.DropOffset(PlaceableKind.Class, nameof(LevelObjectKind.Item), true, Vec3.Zero, Floor));
        Assert.Null(MainWindow.DropOffset(PlaceableKind.Class, nameof(LevelObjectKind.MpRespawnPoint), true, Vec3.Zero, new Vec3(1, 0, 0)));
        Assert.Null(MainWindow.DropOffset(PlaceableKind.Class, nameof(LevelObjectKind.MpRespawnPoint), false, Vec3.Zero, Floor));
        Assert.Null(MainWindow.DropOffset(PlaceableKind.Mesh, "x.v3m", true, Vec3.Zero, Floor));
    }
}

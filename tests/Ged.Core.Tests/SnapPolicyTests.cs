using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Snap-math gates for the magnet toggle (Task 1g): move drags land the pivot on
/// absolute world-grid multiples (not delta quantization), rotate quantizes to the
/// increment, scale steps, and Alt temporarily inverts the active state.
/// </summary>
public sealed class SnapPolicyTests
{
    [Fact]
    public void Move_Snaps_Pivot_To_Absolute_Grid_Multiples()
    {
        var snap = new SnapPolicy { Enabled = true, GridSize = 1f };

        // A pivot that starts off-grid (0.3) dragged +2.2 lands on a grid multiple,
        // not merely pivot + quantized-delta: 0.3 + 2.2 = 2.5 -> 2 (nearest multiple).
        Vec3 moved = snap.MovedPivot(new Vec3(0.3f, 0.3f, 0f), new Vec3(2.2f, 0f, 0f), invert: false);

        Assert.Equal(0f, moved.X % 1f, 4);          // exactly on the grid
        Assert.Equal(2f, moved.X, 4);
        Assert.Equal(0.3f, moved.Y, 4);             // untouched axis is NOT grid-jumped
        Assert.Equal(0f, moved.Z, 4);
    }

    [Fact]
    public void Move_Snaps_On_Half_Meter_Grid()
    {
        var snap = new SnapPolicy { Enabled = true, GridSize = 0.5f };
        Vec3 moved = snap.MovedPivot(new Vec3(0f, 0f, 0f), new Vec3(1.24f, 0f, 0f), invert: false);
        Assert.Equal(1.0f, moved.X, 4); // 1.24 -> nearest 0.5 multiple = 1.0
    }

    [Fact]
    public void Move_Free_When_Disabled()
    {
        var snap = new SnapPolicy { Enabled = false, GridSize = 1f };
        Vec3 moved = snap.MovedPivot(new Vec3(0.3f, 0f, 0f), new Vec3(2.2f, 0f, 0f), invert: false);
        Assert.Equal(2.5f, moved.X, 4); // continuous
    }

    [Fact]
    public void Alt_Temporarily_Inverts_Snap_State()
    {
        var on = new SnapPolicy { Enabled = true, GridSize = 1f };
        // Alt held while snap is ON -> free move.
        Vec3 free = on.MovedPivot(new Vec3(0.3f, 0f, 0f), new Vec3(2.2f, 0f, 0f), invert: true);
        Assert.Equal(2.5f, free.X, 4);

        var off = new SnapPolicy { Enabled = false, GridSize = 1f };
        // Alt held while snap is OFF -> snapped move.
        Vec3 snapped = off.MovedPivot(new Vec3(0.3f, 0f, 0f), new Vec3(2.2f, 0f, 0f), invert: true);
        Assert.Equal(2f, snapped.X, 4);
    }

    [Fact]
    public void Rotation_Quantizes_To_Increment()
    {
        var snap = new SnapPolicy { Enabled = true, RotationStepDegrees = 15f };
        Assert.Equal(30f, snap.RotationDegrees(37f, invert: false), 4);   // 37 -> 30
        Assert.Equal(45f, snap.RotationDegrees(41f, invert: false), 4);   // 41 -> 45
        Assert.Equal(41f, snap.RotationDegrees(41f, invert: true), 4);    // Alt -> free
    }

    [Fact]
    public void Scale_Steps_By_ScaleStep()
    {
        var snap = new SnapPolicy { Enabled = true, ScaleStep = 0.05f };
        Assert.Equal(1.15f, snap.ScaleFactor(1.13f, invert: false), 4);   // 1.13 -> 1.15
        Assert.Equal(0.5f, snap.ScaleFactor(0.52f, invert: false), 4);    // 0.52 -> 0.50
        Assert.Equal(1.13f, snap.ScaleFactor(1.13f, invert: true), 4);    // Alt -> free
    }

    [Fact]
    public void IsActive_Honours_Enabled_Xor_Invert()
    {
        Assert.True(new SnapPolicy { Enabled = true }.IsActive(invert: false));
        Assert.False(new SnapPolicy { Enabled = true }.IsActive(invert: true));
        Assert.False(new SnapPolicy { Enabled = false }.IsActive(invert: false));
        Assert.True(new SnapPolicy { Enabled = false }.IsActive(invert: true));
    }
}

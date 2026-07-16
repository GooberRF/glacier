using System.Collections.Generic;
using Ged.Core.Editing;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 4: the shared grid-size / rotation-increment preset ladders. Hotkey stepping
/// ([ / ] and Shift+[ / Shift+]), the status-bar popovers and the pane toolbar pickers
/// all step through these, and free entry is validated/clamped here.
/// </summary>
public sealed class SnapIncrementsTests
{
    [Fact]
    public void Grid_Ladders_Match_The_Spec()
    {
        // Quick-select (pickers): powers of two 1/32 m .. 8 m — nothing above 8.
        Assert.Equal(new[] { 0.03125f, 0.0625f, 0.125f, 0.25f, 0.5f, 1f, 2f, 4f, 8f }, SnapIncrements.GridPresets);

        // Hotkey ladder: continues doubling up to 256 m.
        Assert.Equal(
            new[] { 0.03125f, 0.0625f, 0.125f, 0.25f, 0.5f, 1f, 2f, 4f, 8f, 16f, 32f, 64f, 128f, 256f },
            SnapIncrements.GridLadder);

        Assert.Equal(new[] { 1f, 5f, 15f, 30f, 45f, 90f }, SnapIncrements.RotationPresets);
    }

    [Theory]
    [InlineData(1f, 2f)]          // on-ladder steps to the next value
    [InlineData(0.03125f, 0.0625f)]
    [InlineData(4f, 8f)]
    [InlineData(8f, 16f)]         // hotkeys continue past the quick-select cap
    [InlineData(128f, 256f)]
    [InlineData(256f, 256f)]      // top of the ladder: stays
    [InlineData(0.3f, 0.5f)]      // free-entry off-ladder value: nearest neighbour upward
    [InlineData(300f, 300f)]      // above the ladder: stays (free entry preserved)
    public void Grid_StepUp_Walks_The_Full_Hotkey_Ladder(float current, float expected) =>
        Assert.Equal(expected, SnapIncrements.StepUp(SnapIncrements.GridLadder, current), 4);

    [Theory]
    [InlineData(2f, 1f)]
    [InlineData(0.0625f, 0.03125f)]
    [InlineData(0.03125f, 0.03125f)] // bottom of the ladder: stays
    [InlineData(0.3f, 0.25f)]        // off-ladder value: nearest neighbour downward
    [InlineData(16f, 8f)]
    [InlineData(300f, 256f)]         // above the ladder: steps back onto it
    public void Grid_StepDown_Walks_The_Full_Hotkey_Ladder(float current, float expected) =>
        Assert.Equal(expected, SnapIncrements.StepDown(SnapIncrements.GridLadder, current), 4);

    [Theory]
    [InlineData(15f, 30f)]
    [InlineData(90f, 90f)]
    [InlineData(20f, 30f)]
    public void Rotation_StepUp_Walks_The_Ladder(float current, float expected) =>
        Assert.Equal(expected, SnapIncrements.StepUp(SnapIncrements.RotationPresets, current), 4);

    [Theory]
    [InlineData(15f, 5f)]
    [InlineData(1f, 1f)]
    [InlineData(20f, 15f)]
    public void Rotation_StepDown_Walks_The_Ladder(float current, float expected) =>
        Assert.Equal(expected, SnapIncrements.StepDown(SnapIncrements.RotationPresets, current), 4);

    [Fact]
    public void Stepping_Up_Then_Down_Is_Stable_On_The_Ladder()
    {
        float v = 1f;
        v = SnapIncrements.StepUp(SnapIncrements.GridLadder, v);
        v = SnapIncrements.StepDown(SnapIncrements.GridLadder, v);
        Assert.Equal(1f, v, 4);
    }

    // ---- Free-entry validation ------------------------------------------------

    [Theory]
    [InlineData("0.25", 0.25f)]
    [InlineData(" 2 ", 2f)]
    [InlineData("0.03125", 0.03125f)] // RED's 1/32 m is representable via free entry
    [InlineData("1000", 256f)]        // clamped to the grid max
    [InlineData("0.001", 0.01f)]      // clamped to the grid min
    public void Grid_Free_Entry_Accepts_And_Clamps(string text, float expected)
    {
        Assert.True(SnapIncrements.TryParseGrid(text, out float v));
        Assert.Equal(expected, v, 5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Grid_Free_Entry_Rejects_Invalid_Input(string? text) =>
        Assert.False(SnapIncrements.TryParseGrid(text, out _));

    [Theory]
    [InlineData("45", 45f)]
    [InlineData("22.5", 22.5f)]
    [InlineData("720", 180f)] // clamped to the rotation max
    [InlineData("0.2", 1f)]   // clamped to the rotation min
    public void Rotation_Free_Entry_Accepts_And_Clamps(string text, float expected)
    {
        Assert.True(SnapIncrements.TryParseRotation(text, out float v));
        Assert.Equal(expected, v, 5);
    }

    [Theory]
    [InlineData("-15")]
    [InlineData("bogus")]
    public void Rotation_Free_Entry_Rejects_Invalid_Input(string text) =>
        Assert.False(SnapIncrements.TryParseRotation(text, out _));
}

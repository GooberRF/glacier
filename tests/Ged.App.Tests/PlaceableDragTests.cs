using Avalonia;
using Ged.App;
using Ged.Core.Editor;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item E / defect 3 — the drag-out gesture recognizer (press → movement-threshold) and the
/// placeable-drop descriptor round-trip. The real OLE drag + drop routing is not headless-testable,
/// so these cover the extracted, deterministic pieces: the threshold gate that must start a drag
/// without stealing a plain click/double-click, and the descriptor parse the drop handler depends on.
/// </summary>
public sealed class PlaceableDragTests
{
    [Fact]
    public void Drag_Starts_Only_After_Crossing_The_Threshold_And_Once_Per_Press()
    {
        var g = new DragGesture(threshold: 5.0);
        Assert.False(g.Move(new Point(0, 0))); // not armed → no drag

        g.Press(new Point(10, 10));
        Assert.True(g.Armed);

        // A tiny wobble (a click / double-click) stays below the threshold → no drag, still armed.
        Assert.False(g.Move(new Point(12, 11))); // |2| + |1| = 3 < 5
        Assert.True(g.Armed);

        // Crossing the threshold starts a drag exactly once, then disarms.
        Assert.True(g.Move(new Point(16, 12))); // |6| + |2| = 8 >= 5
        Assert.False(g.Armed);
        Assert.False(g.Move(new Point(40, 40))); // no second drag from the same press
    }

    [Fact]
    public void Release_Disarms_The_Gesture()
    {
        var g = new DragGesture();
        g.Press(new Point(0, 0));
        g.Release();
        Assert.False(g.Armed);
        Assert.False(g.Move(new Point(100, 100)));
    }

    [Theory]
    [InlineData(LevelObjectKind.Clutter, "Barrel")]
    [InlineData(LevelObjectKind.Item, "med_kit")]
    [InlineData(LevelObjectKind.Entity, null)]
    public void Class_Descriptor_Round_Trips(LevelObjectKind kind, string? className)
    {
        Assert.True(PlaceableDrag.TryParse(PlaceableDrag.Class(kind, className), out PlaceableKind pk, out string arg1, out string? arg2));
        Assert.Equal(PlaceableKind.Class, pk);
        Assert.Equal(kind.ToString(), arg1);
        Assert.Equal(className, arg2);
    }

    [Fact]
    public void Mesh_And_Prefab_Descriptors_Round_Trip()
    {
        Assert.True(PlaceableDrag.TryParse(PlaceableDrag.Mesh("foo.v3m"), out PlaceableKind mk, out string mArg, out _));
        Assert.Equal(PlaceableKind.Mesh, mk);
        Assert.Equal("foo.v3m", mArg);

        Assert.True(PlaceableDrag.TryParse(PlaceableDrag.Prefab(@"C:\p\door.gedprefab"), out PlaceableKind pk, out string pArg, out _));
        Assert.Equal(PlaceableKind.Prefab, pk);
        Assert.Equal(@"C:\p\door.gedprefab", pArg);
    }

    [Fact]
    public void Unparsable_Descriptors_Are_Rejected()
    {
        Assert.False(PlaceableDrag.TryParse("garbage", out _, out _, out _));
        Assert.False(PlaceableDrag.TryParse("mesh", out _, out _, out _)); // missing argument
    }
}

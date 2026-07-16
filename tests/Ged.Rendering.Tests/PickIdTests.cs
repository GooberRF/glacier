using Ged.Core.Editing;
using Ged.Rendering.Picking;
using Xunit;

namespace Ged.Rendering.Tests;

public sealed class PickIdTests
{
    [Theory]
    [InlineData(GizmoHandle.MoveX)]
    [InlineData(GizmoHandle.PlaneXY)]
    [InlineData(GizmoHandle.RotateZ)]
    [InlineData(GizmoHandle.ScaleY)]
    [InlineData(GizmoHandle.ScaleUniform)]
    public void GizmoHandle_RoundTrips_Through_A_Gizmo_PickId(GizmoHandle handle)
    {
        // Each manipulator handle carries its own PickKind.Gizmo id.
        var id = new PickId(PickKind.Gizmo, (int)handle);
        PickId decoded = PickId.Decode(id.Encode());

        Assert.Equal(PickKind.Gizmo, decoded.Kind);
        Assert.Equal(handle, (GizmoHandle)decoded.Index);
    }

    [Theory]
    [InlineData(PickKind.Face, 0)]
    [InlineData(PickKind.Face, 1)]
    [InlineData(PickKind.Face, 50399)]
    [InlineData(PickKind.Object, 12345)]
    [InlineData(PickKind.Brush, 65535)]
    [InlineData(PickKind.Mesh, 0x0FFFFFFF)]
    public void EncodeDecode_RoundTrips(PickKind kind, int index)
    {
        var id = new PickId(kind, index);
        uint encoded = id.Encode();
        PickId decoded = PickId.Decode(encoded);

        Assert.Equal(kind, decoded.Kind);
        Assert.Equal(index, decoded.Index);
    }

    [Fact]
    public void None_EncodesToZero_AndDecodesBack()
    {
        Assert.Equal(0u, PickId.None.Encode());
        Assert.True(PickId.Decode(0).IsNone);
    }

    [Fact]
    public void RealPick_NeverEncodesToZero()
    {
        // Face 0 must not collide with the "nothing" sentinel.
        Assert.NotEqual(0u, new PickId(PickKind.Face, 0).Encode());
    }

    [Fact]
    public void KindOccupiesHighNibble()
    {
        uint encoded = new PickId(PickKind.Object, 7).Encode();
        Assert.Equal((uint)PickKind.Object, encoded >> 28);
        Assert.Equal(7u, encoded & 0x0FFFFFFF);
    }

    [Fact]
    public void PayloadOverflow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PickId(PickKind.Face, 0x10000000).Encode());
    }
}

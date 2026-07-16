using Ged.Core.IO;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>Unit tests for the low-level binary reader/writer primitives.</summary>
public sealed class PrimitiveTests
{
    [Fact]
    public void VString_RoundTrips_Including_High_Bytes()
    {
        var w = new RfWriter();
        // Includes 0xAB (the trigger PF marker) and other high bytes.
        string original = "textures\\«ÿend";
        w.WriteVString(original);
        byte[] bytes = w.ToArray();

        var r = new RfReader(bytes);
        Assert.Equal(original, r.ReadVString());
        Assert.Equal(bytes.Length, r.Position);
    }

    [Fact]
    public void Floats_RoundTrip_BitExact_Including_Special_Values()
    {
        float[] values = { 0f, -0f, 1.5f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, -123456.789f };
        var w = new RfWriter();
        foreach (float v in values)
        {
            w.WriteF32(v);
        }

        byte[] bytes = w.ToArray();
        var r = new RfReader(bytes);
        foreach (float v in values)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(v), BitConverter.SingleToInt32Bits(r.ReadF32()));
        }
    }

    [Fact]
    public void Mat3_Preserves_Row_Order_Forward_Right_Up()
    {
        var m = new Mat3(new Vec3(1, 2, 3), new Vec3(4, 5, 6), new Vec3(7, 8, 9));
        var w = new RfWriter();
        w.WriteMat3(m);
        var r = new RfReader(w.ToArray());
        Assert.Equal(m, r.ReadMat3());
    }

    [Fact]
    public void Integer_Types_Are_LittleEndian()
    {
        var w = new RfWriter();
        w.WriteU32(0xD4BADA55);
        byte[] bytes = w.ToArray();
        Assert.Equal(new byte[] { 0x55, 0xDA, 0xBA, 0xD4 }, bytes);
    }
}

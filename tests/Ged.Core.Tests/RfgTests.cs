using Ged.Core.IO.Rfg;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The corpus contains no .rfg files, so the RFG reader/writer are validated as
/// mutual inverses: build a document, save, reload, and save again, asserting
/// the two byte streams are identical and the round-tripped model matches.
/// </summary>
public sealed class RfgTests
{
    [Theory]
    [InlineData(0xC8)]   // stock
    [InlineData(0x12C)]  // Alpine v300 (adds per-brush metadata chunk)
    public void Rfg_RoundTrips_ByteStable(int version)
    {
        RfgFile original = BuildSample(version);

        byte[] first = original.Save();
        RfgFile reloaded = RfgFile.Load(first);
        byte[] second = reloaded.Save();

        Assert.True(first.AsSpan().SequenceEqual(second),
            $"v0x{version:X}: RFG save/reload/save was not byte-stable.");

        Assert.Equal(RfgFile.Magic, System.BitConverter.ToUInt32(first, 0));
        Assert.Equal(original.Version, reloaded.Version);
        Assert.Equal(original.Groups.Count, reloaded.Groups.Count);

        RfgGroup g0 = reloaded.Groups[0];
        Assert.Equal("test group", g0.Name);
        Assert.Single(g0.Lights.Lights);
        Assert.Equal(0x1234, g0.Lights.Lights[0].Uid);
        Assert.Single(g0.Brushes.Brushes);
        Assert.Equal("Teleport", g0.Events.Events[0].ClassName);
        Assert.Single(g0.NavPoints);
        Assert.Equal(77, g0.NavPoints[0].Uid);

        if (version >= 0x12C)
        {
            Assert.Single(g0.AlpineBrushInfos);
            Assert.Equal(2u, g0.AlpineBrushInfos[0].BrushIndex);
            Assert.True(g0.AlpineBrushInfos[0].IsBreakable);
        }
    }

    private static RfgFile BuildSample(int version)
    {
        var file = new RfgFile { Version = version };
        var group = new RfgGroup { Name = "test group", IsMoving = 0 };

        group.Brushes.Brushes.Add(new Brush
        {
            Uid = 5,
            Position = new Vec3(1, 2, 3),
            Rotation = Mat3.Identity,
            Flags = 0x2,
            Life = -1,
            State = 0,
        });

        group.Lights.Lights.Add(new Light
        {
            Uid = 0x1234,
            ClassName = "Light",
            Rotation = Mat3.Identity,
            Color = new RfColor(255, 200, 100, 255),
            Range = 10f,
        });

        // Directional event: exercises the version-gated rot in the embedded
        // events section (Teleport carries rot at version >= 0x91).
        group.Events.Events.Add(new RflEvent
        {
            Uid = 42,
            ClassName = "Teleport",
            Position = new Vec3(4, 5, 6),
            Rotation = Mat3.Identity,
            Delay = 1.5f,
            Color = new RfColor(1, 2, 3, 4),
        });

        group.NavPoints.Add(new NavPoint { Uid = 77, Height = 1f, Radius = 2f });

        if (version >= 0x12C)
        {
            group.AlpineBrushInfos.Add(new AlpineBrushInfo { BrushIndex = 2, Flags = 0x2, Material = 3 });
        }

        file.Groups.Add(group);
        return file;
    }
}

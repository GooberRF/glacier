using System.Buffers.Binary;
using System.Text;
using Ged.Core.IO.Vpp;
using Xunit;

namespace Ged.Core.Tests;

public class VppTests
{
    [Fact]
    public void Builder_RoundTrips_Contents_And_Order()
    {
        var a = Encoding.ASCII.GetBytes("hello world");
        var b = new byte[3000]; // spans more than one alignment block
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = (byte)(i * 7);
        }

        var empty = Array.Empty<byte>();

        byte[] archive = new VppBuilder()
            .Add("first.txt", a)
            .Add("second.bin", b)
            .Add("empty.dat", empty)
            .ToArray();

        using var vpp = VppArchive.Open(new MemoryStream(archive));

        Assert.Equal(3, vpp.Entries.Count);
        Assert.Equal(new[] { "first.txt", "second.bin", "empty.dat" }, vpp.Entries.Select(e => e.Name).ToArray());
        Assert.Equal(a, vpp.Read("first.txt"));
        Assert.Equal(b, vpp.Read("second.bin"));
        Assert.Equal(empty, vpp.Read("empty.dat"));
    }

    [Fact]
    public void Builder_Produces_Spec_Compliant_Layout()
    {
        var data = Encoding.ASCII.GetBytes("payload");
        byte[] archive = new VppBuilder().Add("a.txt", data).ToArray();

        // Header.
        Assert.Equal(VppFormat.Signature, BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(0)));
        Assert.Equal(VppFormat.Version, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(8)));
        Assert.Equal(archive.Length, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(12)));

        // Everything is 2048-aligned: header block + 1-entry table block + 1 file block.
        Assert.Equal(3 * VppFormat.Alignment, archive.Length);

        // Directory begins at offset 2048, not 16.
        string name = Encoding.Latin1.GetString(archive.AsSpan(VppFormat.Alignment, 5));
        Assert.Equal("a.txt", name);
        Assert.Equal(data.Length, BinaryPrimitives.ReadInt32LittleEndian(
            archive.AsSpan(VppFormat.Alignment + VppFormat.NameFieldSize)));

        // File data lands at the third block; header/table padding is zero-filled.
        Assert.Equal(data, archive.AsSpan(2 * VppFormat.Alignment, data.Length).ToArray());
        for (int i = VppFormat.HeaderSize; i < VppFormat.Alignment; i++)
        {
            Assert.Equal(0, archive[i]);
        }
    }

    [Fact]
    public void ComputeArchiveSize_Matches_Output_Length()
    {
        var builder = new VppBuilder()
            .Add("one", new byte[100])
            .Add("two", new byte[5000])
            .Add("three", new byte[2048]);

        Assert.Equal(builder.ComputeArchiveSize(), builder.ToArray().Length);
    }

    [Fact]
    public void Contains_And_Find_Are_Case_Insensitive()
    {
        byte[] archive = new VppBuilder().Add("Tank.V3M", new byte[8]).ToArray();
        using var vpp = VppArchive.Open(new MemoryStream(archive));

        Assert.True(vpp.Contains("tank.v3m"));
        Assert.NotNull(vpp.Find("TANK.V3M"));
        Assert.False(vpp.Contains("nope.v3m"));
    }

    [Fact]
    public void Add_Rejects_Duplicate_And_Overlong_Names()
    {
        var builder = new VppBuilder().Add("dup", new byte[1]);
        Assert.Throws<ArgumentException>(() => builder.Add("dup", new byte[1]));
        Assert.Throws<ArgumentException>(() => builder.Add(new string('x', 60), new byte[1]));
    }

    [Fact]
    public void Open_Rejects_Bad_Signature()
    {
        var bytes = new byte[VppFormat.Alignment];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0xDEADBEEF);
        Assert.Throws<VppFormatException>(() => VppArchive.Open(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData("tables.vpp")]
    [InlineData("meshes.vpp")]
    public void Parses_Real_Game_Vpp(string vppName)
    {
        string? path = TestPaths.RfVpp(vppName);
        if (path is null)
        {
            return; // real RF install not present on this machine; skip gracefully
        }

        using var vpp = VppArchive.Open(path);

        Assert.NotEmpty(vpp.Entries);
        Assert.True(vpp.Entries.Count <= VppFormat.MaxFiles);
        // archive_size recorded in the header equals the file's real length.
        Assert.Equal(new FileInfo(path).Length, vpp.ArchiveSize);

        // Every entry name is non-empty and every data region is inside the file.
        long fileLen = new FileInfo(path).Length;
        foreach (VppEntry e in vpp.Entries)
        {
            Assert.False(string.IsNullOrEmpty(e.Name));
            Assert.True(e.Offset + e.Size <= fileLen);
        }
    }

    [Fact]
    public void Extracted_File_From_Real_Vpp_Has_Expected_Magic()
    {
        string? path = TestPaths.RfVpp("meshes.vpp");
        if (path is null)
        {
            return;
        }

        using var vpp = VppArchive.Open(path);

        // Pull any .v3m and confirm its 'RF3D' signature; any .v3c and confirm 'RFCM'.
        VppEntry? v3m = vpp.Entries.FirstOrDefault(e => e.Name.EndsWith(".v3m", StringComparison.OrdinalIgnoreCase));
        if (v3m is not null)
        {
            byte[] data = vpp.Read(v3m);
            Assert.Equal(0x52463344u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0)));
        }

        VppEntry? v3c = vpp.Entries.FirstOrDefault(e => e.Name.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase));
        if (v3c is not null)
        {
            byte[] data = vpp.Read(v3c);
            Assert.Equal(0x5246434Du, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0)));
        }
    }

    [Fact]
    public void Reparsing_A_Real_Vpps_Extract_Then_Repack_Is_Structurally_Identical()
    {
        string? path = TestPaths.RfVpp("tables.vpp");
        if (path is null)
        {
            return;
        }

        using var original = VppArchive.Open(path);
        var builder = new VppBuilder();
        foreach (VppEntry e in original.Entries)
        {
            builder.Add(e.Name, original.Read(e));
        }

        byte[] rebuilt = builder.ToArray();
        using var reparsed = VppArchive.Open(new MemoryStream(rebuilt));

        Assert.Equal(original.Entries.Count, reparsed.Entries.Count);
        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Name, reparsed.Entries[i].Name);
            Assert.Equal(original.Entries[i].Size, reparsed.Entries[i].Size);
            Assert.Equal(original.Read(original.Entries[i]), reparsed.Read(reparsed.Entries[i]));
        }

        // Our writer zero-pads exactly like retail Volition packs, so the whole archive is byte-identical.
        Assert.Equal(File.ReadAllBytes(path), rebuilt);
    }
}

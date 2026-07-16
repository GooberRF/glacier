using System.Buffers.Binary;
using System.IO;
using System.Text;
using Ged.Core.IO.Tex;
using Xunit;

namespace Ged.Core.Tests;

public class TextureTests
{
    // ─── TGA writer ─────────────────────────────────────────────────────────

    [Fact]
    public void TgaWriter_RoundTrips_Through_Decoder()
    {
        var pixels = new byte[]
        {
            255, 0, 0, 255,   0, 255, 0, 128,   // red (opaque), green (half-alpha)
            0, 0, 255, 255,   255, 255, 0, 64,  // blue, yellow
        };
        var img = new TextureImage(2, 2, pixels);

        byte[] tga = TgaWriter.Encode(img);
        Assert.True(TgaDecoder.CanDecode(tga));
        TextureImage back = TgaDecoder.Decode(tga).Primary;

        Assert.Equal(2, back.Width);
        Assert.Equal(2, back.Height);
        Assert.Equal(pixels, back.Pixels); // top-left origin, RGBA preserved incl. alpha
    }

    // ─── TGA ────────────────────────────────────────────────────────────────

    [Fact]
    public void Tga_24bit_BottomOrigin_Flips_And_Reorders_To_Rgba()
    {
        // Image we want (top-left origin): (0,0)=red (1,0)=green / (0,1)=blue (1,1)=yellow.
        // File is bottom-origin, so file row 0 is the bottom image row, BGR order.
        byte[] pixels =
        {
            0xFF, 0x00, 0x00, // blue  (B,G,R)
            0x00, 0xFF, 0xFF, // yellow
            0x00, 0x00, 0xFF, // red
            0x00, 0xFF, 0x00, // green
        };
        byte[] tga = MakeTga(2, 2, imageType: 2, depth: 24, descriptor: 0x00, pixels);

        DecodedTexture tex = TgaDecoder.Decode(tga);
        TextureImage img = tex.Primary;

        Assert.Equal(2, img.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal((255, 0, 0, 255), img.GetPixel(0, 0));
        Assert.Equal((0, 255, 0, 255), img.GetPixel(1, 0));
        Assert.Equal((0, 0, 255, 255), img.GetPixel(0, 1));
        Assert.Equal((255, 255, 0, 255), img.GetPixel(1, 1));
    }

    [Fact]
    public void Tga_32bit_Preserves_Alpha()
    {
        byte[] pixels = { 0x10, 0x20, 0x30, 0x40 }; // B,G,R,A
        byte[] tga = MakeTga(1, 1, imageType: 2, depth: 32, descriptor: 0x20, pixels);

        TextureImage img = TgaDecoder.Decode(tga).Primary;
        Assert.Equal((0x30, 0x20, 0x10, 0x40), img.GetPixel(0, 0));
    }

    [Fact]
    public void Tga_8bit_Greyscale_Type3_Expands_To_Rgb()
    {
        byte[] pixels = { 10, 20, 30, 40 };
        byte[] tga = MakeTga(2, 2, imageType: 3, depth: 8, descriptor: 0x20, pixels);

        TextureImage img = TgaDecoder.Decode(tga).Primary;
        Assert.Equal((10, 10, 10, 255), img.GetPixel(0, 0));
        Assert.Equal((40, 40, 40, 255), img.GetPixel(1, 1));
    }

    [Fact]
    public void Tga_Rle_TrueColor_Type10_Decodes_Run()
    {
        // One RLE run packet covering all 4 pixels of a 2x2 image, colour = red (BGR).
        byte[] rle = { 0x83, 0x00, 0x00, 0xFF }; // count-1=3 -> 4 pixels, run bit set
        byte[] tga = MakeTga(2, 2, imageType: 10, depth: 24, descriptor: 0x20, rle);

        TextureImage img = TgaDecoder.Decode(tga).Primary;
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                Assert.Equal((255, 0, 0, 255), img.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void Tga_Rle_Greyscale_Type11_Decodes()
    {
        // Raw packet of 2 grey pixels, then a run of 2 grey pixels.
        byte[] rle = { 0x01, 100, 150, 0x81, 200 }; // raw(2): 100,150 ; run(2): 200
        byte[] tga = MakeTga(2, 2, imageType: 11, depth: 8, descriptor: 0x20, rle);

        TextureImage img = TgaDecoder.Decode(tga).Primary;
        Assert.Equal((100, 100, 100, 255), img.GetPixel(0, 0));
        Assert.Equal((150, 150, 150, 255), img.GetPixel(1, 0));
        Assert.Equal((200, 200, 200, 255), img.GetPixel(0, 1));
        Assert.Equal((200, 200, 200, 255), img.GetPixel(1, 1));
    }

    [Fact]
    public void Tga_Real_Game_Fixture_Decodes()
    {
        string? path = TestPaths.FixtureFile("tex", "mtl_bluefiller01.tga");
        if (path is null)
        {
            return; // retail-derived fixture not present
        }

        byte[] data = File.ReadAllBytes(path);
        DecodedTexture tex = TgaDecoder.Decode(data);
        Assert.Equal(16, tex.Width);
        Assert.Equal(16, tex.Height);
        Assert.Equal(TextureFormatKind.Tga, tex.SourceFormat);
        // 24-bit source: every pixel fully opaque.
        for (int i = 3; i < tex.Primary.Pixels.Length; i += 4)
        {
            Assert.Equal(255, tex.Primary.Pixels[i]);
        }
    }

    // ─── VBM ────────────────────────────────────────────────────────────────

    [Fact]
    public void Vbm_1555_Decodes_Alpha_Bit_And_Channels()
    {
        // 2x1: pixel0 = opaque white (0xFFFF), pixel1 = transparent red (A=0,R=31).
        ushort white = 0xFFFF;
        ushort transRed = 0x7C00; // A=0, R=31, G=0, B=0
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), white);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), transRed);
        byte[] vbm = MakeVbm(version: 1, width: 2, height: 1, format: 0, fps: 0, frames: 1, mipmaps: 0, body);

        TextureImage img = VbmDecoder.Decode(vbm).Primary;
        Assert.Equal((255, 255, 255, 255), img.GetPixel(0, 0));
        Assert.Equal((255, 0, 0, 0), img.GetPixel(1, 0));
    }

    [Fact]
    public void Vbm_Real_Animated_565_Fixture()
    {
        // fighter01_lb.vbm: v1, 32x16, format 565, 2 frames, 0 mips, fps 6.
        string? path = TestPaths.FixtureFile("tex", "fighter01_lb.vbm");
        if (path is null)
        {
            return; // retail-derived fixture not present
        }

        byte[] data = File.ReadAllBytes(path);
        DecodedTexture tex = VbmDecoder.Decode(data);
        Assert.Equal(32, tex.Width);
        Assert.Equal(16, tex.Height);
        Assert.Equal(2, tex.FrameCount);
        Assert.Equal(1, tex.MipCount);
        Assert.Equal(6, tex.Fps);
        // 565 has no alpha channel: opaque everywhere.
        Assert.Equal(255, tex.Primary.GetPixel(0, 0).A);
    }

    [Fact]
    public void Vbm_Real_Mipmapped_4444_Fixture()
    {
        // mtl_fence02_A.vbm: v2, 32x32, format 4444, 1 frame, 4 mips.
        string? path = TestPaths.FixtureFile("tex", "mtl_fence02_A.vbm");
        if (path is null)
        {
            return; // retail-derived fixture not present
        }

        byte[] data = File.ReadAllBytes(path);
        DecodedTexture tex = VbmDecoder.Decode(data);
        Assert.Equal(32, tex.Width);
        Assert.Equal(32, tex.Height);
        Assert.Equal(1, tex.FrameCount);
        Assert.Equal(5, tex.MipCount); // num_mipmaps(4) + level 0
    }

    [Fact]
    public void Vbm_Real_Single_4444_Fixture()
    {
        string? path = TestPaths.FixtureFile("tex", "hud_microflag_red.vbm");
        if (path is null)
        {
            return; // retail-derived fixture not present
        }

        byte[] data = File.ReadAllBytes(path);
        DecodedTexture tex = VbmDecoder.Decode(data);
        Assert.Equal(8, tex.Width);
        Assert.Equal(9, tex.Height);
        Assert.Equal(1, tex.FrameCount);
    }

    // ─── DDS ────────────────────────────────────────────────────────────────

    [Fact]
    public void Dds_Bc1_Opaque_Block_Decodes()
    {
        // 4x4 block, c0 = red565 (> c1=0) -> 4-colour mode, all indices 0 -> all red.
        byte[] block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0), 0xF800); // c0 red
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), 0x0000); // c1 black
        // indices already zero
        byte[] dds = MakeDdsFourCc(4, 4, "DXT1", block);

        DecodedTexture tex = DdsDecoder.Decode(dds);
        Assert.Equal(4, tex.Width);
        Assert.Equal((255, 0, 0, 255), tex.Primary.GetPixel(0, 0));
        Assert.Equal((255, 0, 0, 255), tex.Primary.GetPixel(3, 3));
    }

    [Fact]
    public void Dds_Bc2_Explicit_Alpha_Decodes()
    {
        byte[] block = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            block[i] = 0xFF; // all alpha nibbles = F -> 255
        }

        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 0xF800); // red
        DecodedTexture tex = DdsDecoder.Decode(MakeDdsFourCc(4, 4, "DXT3", block));
        Assert.Equal((255, 0, 0, 255), tex.Primary.GetPixel(1, 1));
    }

    [Fact]
    public void Dds_Bc3_Interpolated_Alpha_Decodes()
    {
        byte[] block = new byte[16];
        block[0] = 255; // a0
        block[1] = 0;   // a1 (a0 > a1) ; indices all 0 -> alpha = a0 = 255
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 0xF800); // red
        DecodedTexture tex = DdsDecoder.Decode(MakeDdsFourCc(4, 4, "DXT5", block));
        Assert.Equal((255, 0, 0, 255), tex.Primary.GetPixel(2, 2));
    }

    [Fact]
    public void Dds_Uncompressed_A8R8G8B8_Decodes()
    {
        // One pixel A=0x40 R=0x10 G=0x20 B=0x30 => uint 0x40102030, stored little-endian.
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x40102030u);
        byte[] dds = MakeDdsRgba(1, 1, body);

        TextureImage img = DdsDecoder.Decode(dds).Primary;
        Assert.Equal((0x10, 0x20, 0x30, 0x40), img.GetPixel(0, 0));
    }

    [Fact]
    public void Dds_Real_Dxt1_Fixture_Decodes()
    {
        string? path = TestPaths.FixtureFile("tex", "catan_sheep_dxt1.dds");
        if (path is null)
        {
            return; // retail-derived fixture not present
        }

        byte[] data = File.ReadAllBytes(path);
        DecodedTexture tex = DdsDecoder.Decode(data);
        Assert.Equal(32, tex.Width);
        Assert.Equal(32, tex.Height);
        Assert.Equal(TextureFormatKind.Dds, tex.SourceFormat);
        Assert.Equal(32 * 32 * 4, tex.Primary.Pixels.Length);
    }

    // ─── PNG / JPG ──────────────────────────────────────────────────────────

    [Fact]
    public void Png_Decodes_Exact_Rgba()
    {
        byte[] data = File.ReadAllBytes(TestPaths.Fixture("tex", "gradient2x2.png"));
        DecodedTexture tex = TextureDecoder.Decode(data);
        Assert.Equal(TextureFormatKind.Png, tex.SourceFormat);
        Assert.Equal((255, 0, 0, 255), tex.Primary.GetPixel(0, 0));
        Assert.Equal((0, 255, 0, 128), tex.Primary.GetPixel(1, 0));
        Assert.Equal((0, 0, 255, 255), tex.Primary.GetPixel(0, 1));
        Assert.Equal((255, 255, 255, 0), tex.Primary.GetPixel(1, 1));
    }

    [Fact]
    public void Jpg_Decodes_Dimensions_And_Approx_Color()
    {
        byte[] data = File.ReadAllBytes(TestPaths.Fixture("tex", "solid8x8.jpg"));
        DecodedTexture tex = TextureDecoder.Decode(data);
        Assert.Equal(TextureFormatKind.Jpeg, tex.SourceFormat);
        Assert.Equal(8, tex.Width);
        Assert.Equal(8, tex.Height);
        var (r, g, b, a) = tex.Primary.GetPixel(4, 4);
        Assert.Equal(255, a);
        Assert.InRange(r, 20, 60);
        Assert.InRange(g, 140, 180);
        Assert.InRange(b, 180, 220);
    }

    // ─── Supercede chain ────────────────────────────────────────────────────

    [Fact]
    public void SupercedeChain_Prefers_Higher_Priority_Extension()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wall.tga", "wall.dds", "wall.vbm" };
        string? winner = SupercedeChain.Resolve("wall.tga", present.Contains);
        Assert.Equal("wall.dds", winner);
    }

    [Fact]
    public void SupercedeChain_Falls_Through_To_Only_Available()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "floor.tga" };
        Assert.Equal("floor.tga", SupercedeChain.Resolve("floor.png", present.Contains));
        Assert.Null(SupercedeChain.Resolve("missing.tga", present.Contains));
    }

    [Fact]
    public void SupercedeChain_Atx_Wins_Over_Everything()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "anim.atx", "anim.dds", "anim.tga" };
        Assert.Equal("anim.atx", SupercedeChain.Resolve("anim", present.Contains));
    }

    // ─── ATX ────────────────────────────────────────────────────────────────

    [Fact]
    public void Atx_Parses_Header_And_Frames()
    {
        const string toml =
            "[header]\n" +
            "frame_time = 1000 # ms\n" +
            "animation_mode = 2 # loop\n" +
            "initially_on = true\n" +
            "\n" +
            "[[frame]]\n" +
            "file = \"a.tga\"\n" +
            "frame_time = 5000\n" +
            "\n" +
            "[[frame]]\n" +
            "file = \"b.tga\"\n";

        AtxDescriptor atx = AtxDescriptor.Parse(toml);
        Assert.Equal(1000, atx.FrameTimeMs);
        Assert.Equal(AtxAnimationMode.Loop, atx.AnimationMode);
        Assert.True(atx.InitiallyOn);
        Assert.Equal(2, atx.Frames.Count);
        Assert.Equal("a.tga", atx.Frames[0].File);
        Assert.Equal(5000, atx.Frames[0].FrameTimeMs);
        Assert.Equal("b.tga", atx.Frames[1].File);
    }

    [Fact]
    public void Atx_Rejects_Nested_Atx_And_Empty()
    {
        Assert.Throws<TextureFormatException>(() =>
            AtxDescriptor.Parse("[[frame]]\nfile = \"loop.atx\"\n"));
        Assert.Throws<TextureFormatException>(() => AtxDescriptor.Parse("[header]\nframe_time = 5\n"));
    }

    [Fact]
    public void Atx_Decodes_Frame0_Via_Resolver()
    {
        // Two 1x1 TGAs; frame 0 red, frame 1 green.
        byte[] red = MakeTga(1, 1, 2, 24, 0x20, new byte[] { 0x00, 0x00, 0xFF });
        byte[] green = MakeTga(1, 1, 2, 24, 0x20, new byte[] { 0x00, 0xFF, 0x00 });
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["r.tga"] = red,
            ["g.tga"] = green,
        };

        AtxDescriptor atx = AtxDescriptor.Parse(
            "[header]\nframe_time = 100\nanimation_mode = 2\n[[frame]]\nfile=\"r.tga\"\n[[frame]]\nfile=\"g.tga\"\n");
        DecodedTexture tex = AtxDecoder.Decode(atx, name => files.GetValueOrDefault(name));

        Assert.Equal(TextureFormatKind.Atx, tex.SourceFormat);
        Assert.Equal(2, tex.FrameCount);
        Assert.Equal((255, 0, 0, 255), tex.Frames[0].GetPixel(0, 0));
        Assert.Equal((0, 255, 0, 255), tex.Frames[1].GetPixel(0, 0));
        Assert.Equal(10, tex.Fps); // 1000 / 100ms
    }

    // ─── Facade ─────────────────────────────────────────────────────────────

    [Fact]
    public void Facade_Sniffs_Format_By_Magic()
    {
        string? vbmPath = TestPaths.FixtureFile("tex", "hud_microflag_red.vbm");
        string? ddsPath = TestPaths.FixtureFile("tex", "catan_sheep_dxt1.dds");
        if (vbmPath is null || ddsPath is null)
        {
            return; // retail-derived fixtures not present
        }

        byte[] vbm = File.ReadAllBytes(vbmPath);
        Assert.Equal(TextureFormatKind.Vbm, TextureDecoder.Decode(vbm).SourceFormat);

        byte[] dds = File.ReadAllBytes(ddsPath);
        Assert.Equal(TextureFormatKind.Dds, TextureDecoder.Decode(dds).SourceFormat);
    }

    // ─── Synthesis helpers ──────────────────────────────────────────────────

    private static byte[] MakeTga(int w, int h, int imageType, int depth, byte descriptor, byte[] body)
    {
        var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[18];
        hdr[2] = (byte)imageType;
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[12..], (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[14..], (ushort)h);
        hdr[16] = (byte)depth;
        hdr[17] = descriptor;
        ms.Write(hdr);
        ms.Write(body);
        return ms.ToArray();
    }

    private static byte[] MakeVbm(int version, int width, int height, int format, int fps, int frames, int mipmaps, byte[] body)
    {
        var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(hdr[0..], 0x6D62762E);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[4..], version);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[8..], width);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[12..], height);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[16..], format);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[20..], fps);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[24..], frames);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[28..], mipmaps);
        ms.Write(hdr);
        ms.Write(body);
        return ms.ToArray();
    }

    private static byte[] MakeDdsFourCc(int w, int h, string fourCc, byte[] body)
    {
        byte[] dds = MakeDdsHeader(w, h);
        uint flags = 0x4; // DDPF_FOURCC
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(80), flags);
        Encoding.ASCII.GetBytes(fourCc).CopyTo(dds.AsSpan(84));
        return Concat(dds, body);
    }

    private static byte[] MakeDdsRgba(int w, int h, byte[] body)
    {
        byte[] dds = MakeDdsHeader(w, h);
        uint flags = 0x41; // DDPF_RGB | DDPF_ALPHAPIXELS
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(80), flags);
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(88), 32); // dwRGBBitCount
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(92), 0x00FF0000); // R
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(96), 0x0000FF00); // G
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(100), 0x000000FF); // B
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(104), 0xFF000000); // A
        return Concat(dds, body);
    }

    private static byte[] MakeDdsHeader(int w, int h)
    {
        var dds = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(0), 0x20534444); // "DDS "
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(4), 124);         // dwSize
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(12), h);          // dwHeight
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(16), w);          // dwWidth
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(28), 1);          // dwMipMapCount
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(76), 32);         // ddspf.dwSize
        return dds;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}

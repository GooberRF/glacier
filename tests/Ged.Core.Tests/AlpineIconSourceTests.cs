using System;
using System.IO;
using Ged.Core.Assets;
using Ged.Core.IO.Tex;
using Ged.Core.IO.Vpp;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 3: the stock Alpine object icons ship only in <c>alpinefaction.vpp</c>, which sits
/// beside <c>AlpineFactionLauncher.exe</c> (possibly outside the RF install). Given the
/// configured launcher path, <see cref="AlpineIconSource"/> opens that vpp directly and
/// resolves the Alpine icons so the original-icons atlas can composite them — otherwise it
/// falls back to null (and the atlas keeps its drawn glyph).
/// </summary>
public sealed class AlpineIconSourceTests : IDisposable
{
    private readonly string _dir;

    public AlpineIconSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ged-alpineicons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static byte[] SolidTga(int w, int h, byte r, byte g, byte b, byte a)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = r;
            px[i + 1] = g;
            px[i + 2] = b;
            px[i + 3] = a;
        }

        return TgaWriter.Encode(new TextureImage(w, h, px));
    }

    /// <summary>Writes a fixture alpinefaction.vpp (with the four Alpine icons) + a fake launcher into the dir.</summary>
    private void WriteFixtureInstall()
    {
        var vpp = new VppBuilder()
            .Add("Icon_AFNote.tga", SolidTga(4, 4, 10, 200, 30, 255))
            .Add("Icon_AFCorona.tga", SolidTga(4, 4, 200, 10, 30, 255))
            .Add("Icon_EAX.tga", SolidTga(4, 4, 30, 10, 200, 255))
            .Add("Icon_Event.tga", SolidTga(4, 4, 200, 200, 10, 255));
        vpp.Write(Path.Combine(_dir, AlpineIconSource.AlpineVppName));
        File.WriteAllText(Path.Combine(_dir, "AlpineFactionLauncher.exe"), string.Empty);
    }

    [Fact]
    public void Resolves_Alpine_Icons_From_The_Vpp_Beside_The_Launcher()
    {
        WriteFixtureInstall();
        string launcher = Path.Combine(_dir, "AlpineFactionLauncher.exe");

        // Configure by the launcher exe path (the settings form): the vpp beside it is found.
        using AlpineIconSource? source = AlpineIconSource.BesideLauncher(launcher);
        Assert.NotNull(source);

        TextureImage? note = source!.Load("Icon_AFNote.tga");
        Assert.NotNull(note);
        Assert.Equal(4, note!.Width);
        (byte r, byte g, byte b, byte a) = note.GetPixel(1, 1);
        Assert.True(g > 150 && r < 60 && a == 255, $"AFNote pixel unexpected: {r},{g},{b},{a}");

        // All four stock Alpine icons resolve.
        Assert.NotNull(source.Load("Icon_AFCorona.tga"));
        Assert.NotNull(source.Load("Icon_EAX.tga"));
        Assert.NotNull(source.Load("Icon_Event.tga"));

        // An unmapped name is a graceful null (the atlas keeps its drawn glyph).
        Assert.Null(source.Load("Icon_DoesNotExist.tga"));
    }

    [Fact]
    public void BesideLauncher_Accepts_A_Directory_Path_Too()
    {
        WriteFixtureInstall();
        using AlpineIconSource? source = AlpineIconSource.BesideLauncher(_dir);
        Assert.NotNull(source);
        Assert.NotNull(source!.Load("Icon_Event.tga"));
    }

    [Fact]
    public void Returns_Null_When_No_Vpp_Or_Blank_Path()
    {
        // Launcher present but no vpp beside it → no source.
        File.WriteAllText(Path.Combine(_dir, "AlpineFactionLauncher.exe"), string.Empty);
        Assert.Null(AlpineIconSource.BesideLauncher(_dir));
        Assert.Null(AlpineIconSource.BesideLauncher(Path.Combine(_dir, "AlpineFactionLauncher.exe")));

        Assert.Null(AlpineIconSource.BesideLauncher(null));
        Assert.Null(AlpineIconSource.BesideLauncher("   "));
        Assert.Null(AlpineIconSource.LocateVpp(_dir));
    }
}

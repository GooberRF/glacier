using System.IO;
using System.Text;
using Ged.Core.Assets;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The case-insensitivity guarantee for LOOSE files: a mixed-case reference
/// (<c>Rck_012.TGA</c>) must resolve to the actual on-disk file (<c>rck_012.tga</c>)
/// even on a case-sensitive filesystem (ext4). On Windows the OS filesystem is
/// case-insensitive, but the logic under test — the <see cref="DirectoryAssetSource"/>
/// snapshot keyed <c>OrdinalIgnoreCase</c> with original-case values — is identical
/// to what runs on Linux, so these assertions exercise the exact ext4 code path.
/// </summary>
public sealed class LooseFileCaseResolverTests : IDisposable
{
    private readonly string _temp;

    public LooseFileCaseResolverTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "ged_case_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void MixedCaseReference_Reads_And_Resolves_Actual_Case()
    {
        // On-disk name is lowercase; the reference is mixed-case (the ext4 scenario).
        File.WriteAllBytes(Path.Combine(_temp, "rck_012.tga"), Encoding.ASCII.GetBytes("PIXELS"));
        var src = new DirectoryAssetSource(_temp, extensions: new[] { ".tga" });

        Assert.True(src.Contains("Rck_012.TGA"));
        Assert.Equal("PIXELS", Encoding.ASCII.GetString(src.Read("Rck_012.TGA")!));

        // The resolver returns the ORIGINAL on-disk case, not the reference case.
        Assert.Equal("rck_012.tga", src.ResolveActualName("Rck_012.TGA"));
        Assert.Null(src.ResolveActualName("missing.tga"));

        using var vfs = new AssetVfs(new IAssetSource[] { src });
        Assert.Equal("rck_012.tga", vfs.ResolveActualName("RCK_012.tga"));
        Assert.Equal("PIXELS", Encoding.ASCII.GetString(vfs.ReadFile("RCK_012.tga")!));
        Assert.EndsWith("rck_012.tga", vfs.ResolveLoosePath("RCK_012.tga"), System.StringComparison.Ordinal);
        Assert.Null(vfs.ResolveActualName("does_not_exist.tga"));
    }

    [Fact]
    public void Reload_Refreshes_The_Snapshot()
    {
        var src = new DirectoryAssetSource(_temp, extensions: new[] { ".tga" });
        using var vfs = new AssetVfs(new IAssetSource[] { src });
        Assert.Null(vfs.ResolveActualName("Sky_01.tga"));

        // A file added after mount is invisible until Reload (Rescan) refreshes the snapshot.
        File.WriteAllBytes(Path.Combine(_temp, "sky_01.tga"), new byte[] { 1 });
        Assert.Null(vfs.ResolveActualName("Sky_01.tga"));

        vfs.Rescan();
        Assert.Equal("sky_01.tga", vfs.ResolveActualName("Sky_01.tga"));
    }
}

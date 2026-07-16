using System;
using System.IO;
using Ged.Core.Assets;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 7: the RF-install directory validation classifier — a directory with/without
/// packfiles, alpine detection, and the inline status text.
/// </summary>
public sealed class RfInstallTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "ged_rf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Missing_Directory_Is_Invalid()
    {
        RfInstallScan scan = RfInstall.Scan(Path.Combine(Path.GetTempPath(), "ged_nope_" + Guid.NewGuid().ToString("N")));
        Assert.False(scan.Exists);
        Assert.False(scan.Valid);
        Assert.Contains("not found", scan.StatusText());
    }

    [Fact]
    public void Empty_Directory_Is_Invalid_With_Guidance()
    {
        string d = TempDir();
        try
        {
            RfInstallScan scan = RfInstall.Scan(d);
            Assert.True(scan.Exists);
            Assert.False(scan.Valid);
            Assert.Equal(0, scan.VppCount);
            Assert.Contains("tables.vpp", scan.StatusText());
        }
        finally
        {
            Directory.Delete(d, true);
        }
    }

    [Fact]
    public void Directory_With_Packfiles_Is_Valid_And_Detects_Alpine()
    {
        string d = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(d, "tables.vpp"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(d, "maps1.vpp"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(d, "alpinefaction.vpp"), Array.Empty<byte>());

            RfInstallScan scan = RfInstall.Scan(d);
            Assert.True(scan.Valid);
            Assert.Equal(3, scan.VppCount);
            Assert.True(scan.HasAlpine);
            Assert.True(scan.HasCorePackfiles);
            Assert.Contains("✓ found 3 VPPs", scan.StatusText());
            Assert.Contains("alpinefaction.vpp", scan.StatusText());
        }
        finally
        {
            Directory.Delete(d, true);
        }
    }

    [Fact]
    public void Non_Core_Packfiles_Are_Still_Valid_But_Not_Core()
    {
        string d = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(d, "custom.vpp"), Array.Empty<byte>());
            RfInstallScan scan = RfInstall.Scan(d);
            Assert.True(scan.Valid);
            Assert.False(scan.HasCorePackfiles);
        }
        finally
        {
            Directory.Delete(d, true);
        }
    }
}

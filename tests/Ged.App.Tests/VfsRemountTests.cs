using System;
using System.IO;
using Ged.App;
using Ged.Core.Assets;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 7: changing the RF install path live-remounts — the old VFS is disposed and a new
/// one mounted, firing VfsChanged so every consumer refreshes. No restart.
/// </summary>
public sealed class VfsRemountTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "ged_mount_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Remount_Swaps_The_Vfs_And_Fires_VfsChanged()
    {
        string dir1 = TempDir();
        string dir2 = TempDir();
        try
        {
            var session = new EditorSession();
            int changes = 0;
            session.VfsChanged += () => changes++;

            session.MountInstall(dir1);
            AssetVfs? first = session.Vfs;
            Assert.NotNull(first);
            Assert.Equal(1, changes);

            // Force a remount to a different directory: a NEW VFS instance + another event.
            session.MountInstall(dir2, force: true);
            Assert.NotSame(first, session.Vfs);
            Assert.NotNull(session.Vfs);
            Assert.Equal(2, changes);

            session.Unmount();
            Assert.Null(session.Vfs);
            Assert.Null(session.RfInstallDir);
            Assert.Equal(3, changes);
        }
        finally
        {
            Directory.Delete(dir1, true);
            Directory.Delete(dir2, true);
        }
    }

    [Fact]
    public void Same_Dir_Without_Force_Does_Not_Remount()
    {
        string dir = TempDir();
        try
        {
            var session = new EditorSession();
            int changes = 0;
            session.VfsChanged += () => changes++;

            session.MountInstall(dir);
            AssetVfs? first = session.Vfs;
            session.MountInstall(dir); // same dir, no force → no-op
            Assert.Same(first, session.Vfs);
            Assert.Equal(1, changes);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

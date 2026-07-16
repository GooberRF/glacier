using System.IO;
using Ged.Core.Assets;

namespace Ged.App;

/// <summary>
/// RF install mounting (item 7): a live remount when the user changes the install path
/// (Settings / wizard) that unmounts the old VFS, mounts the new one and refreshes every
/// consumer without a restart; directory validation with feedback; and a status-bar mount
/// indicator. All mounts flow through <see cref="EditorSession.VfsChanged"/> so one refresh
/// path serves startup, Settings and the wizard.
/// </summary>
public sealed partial class MainWindow
{
    private bool _mountUiReady;

    private void InitMount()
    {
        _session.VfsChanged += OnVfsChanged;
        _mountUiReady = true;
        UpdateMountStatus();
    }

    /// <summary>
    /// Applies a chosen RF install path: validates it, live-remounts (or unmounts an
    /// invalid path), persists, and returns the scan for inline feedback. Every consumer
    /// refreshes via <see cref="EditorSession.VfsChanged"/>.
    /// </summary>
    public RfInstallScan ApplyRfInstall(string? dir)
    {
        RfInstallScan scan = RfInstall.Scan(dir);
        if (scan.Valid && dir is not null)
        {
            _session.MountInstall(dir, force: true);
            _settings.RfInstallDir = dir;
            _dispatcher.ShowMessage($"Mounted RF install: {scan.StatusText()}");
        }
        else
        {
            _session.Unmount();
            _settings.RfInstallDir = dir; // remember the (bad) path so the field shows it
            _dispatcher.ShowMessage($"RF install not mounted: {scan.StatusText()}");
        }

        Persist();
        UpdateMountStatus();
        return scan;
    }

    /// <summary>
    /// Mounts the CONFIGURED install at startup, quietly (no picker, no persist). Without
    /// this the mount was lazy — only File ▸ Open ever mounted — so a fresh launch followed
    /// by File ▸ New (or just opening the palette) had no VFS: no clutter.tbl/items.tbl
    /// catalogs, empty palette Clutter/Items tabs, no original icons, an empty asset
    /// browser. Runs after the first-run wizard so a just-chosen install mounts too; a
    /// missing/invalid configured path is a silent no-op (the Open flow still prompts).
    /// Consumers refresh via <see cref="EditorSession.VfsChanged"/> as with every mount.
    /// </summary>
    private void MountConfiguredInstallAtStartup()
    {
        if (_session.Vfs is not null)
        {
            return;
        }

        string? dir = _settings.RfInstallDir;
        if (dir is null || !RfInstall.Scan(dir).Valid)
        {
            return;
        }

        try
        {
            _session.MountInstall(dir); // raises VfsChanged → OnVfsChanged refreshes every consumer
            CrashHandler.LogInfo("mount", $"startup-mounted RF install: {dir} ({RfInstall.Scan(dir).VppCount} VPPs)");
        }
        catch (System.Exception ex)
        {
            // A mount failure at startup (locked/corrupt VPP) must never block launch;
            // the status bar shows "not mounted" and Settings can re-point the path.
            CrashHandler.LogNonFatal("startup-mount", ex);
        }

        UpdateMountStatus();
    }

    /// <summary>Refreshes every mount consumer after the VFS swaps (item 7).</summary>
    private void OnVfsChanged()
    {
        if (!_mountUiReady)
        {
            return;
        }

        ApplyIconAtlas();
        RefreshAssetBrowser();
        _palette.RefreshCatalogs();
        RebuildScene(); // re-uploads scene textures from the new VFS
        UpdateMountStatus();
    }

    private void UpdateMountStatus()
    {
        if (_session.Vfs is not null && _session.RfInstallDir is { } dir)
        {
            RfInstallScan scan = RfInstall.Scan(dir);
            _statusMount.Text = $"RF: {Path.GetFileName(dir.TrimEnd('\\', '/'))} · {scan.VppCount} VPPs";
        }
        else
        {
            _statusMount.Text = "RF: not mounted — click to configure";
        }
    }
}

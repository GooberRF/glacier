using System;
using System.Linq;
using System.Threading.Tasks;
using Ged.Core.Input;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Packaging;

namespace Ged.App;

/// <summary>
/// File &gt; Create Level Packfile: scans the open level's dependencies against the
/// mounted VFS, presents the review dialog, and writes the VPP (level .rfl first).
/// The scan / plan / dialog steps are exposed on <see cref="Panels.IEditorHost"/> so
/// the Dependency Graph panel can drive them with its own graph include state.
/// </summary>
public sealed partial class MainWindow
{
    private void InitPackfile()
    {
        _dispatcher.Bind(CommandIds.FilePackfile, () => _ = OpenPackfileCommandAsync(), () => Document is not null);
    }

    /// <summary>The level file name (dependency-graph root + packfile name).</summary>
    public string LevelLabel => LevelFileName();

    /// <summary>Scans the open level's dependencies against the mounted VFS (null when unavailable).</summary>
    public async Task<DependencyScanResult?> ScanDependenciesAsync()
    {
        if (Document is not { } doc || _session.Vfs is not { } vfs)
        {
            return null;
        }

        DependencyScanOptions options = _session.BuildScanOptions();
        return await Task.Run(() => DependencyScanner.Scan(doc.Rfl, new VfsDependencyResolver(vfs), options));
    }

    /// <summary>Builds a packfile plan (default output path + level name) from a scan, or null.</summary>
    public PackfileBuildPlan? CreatePackfilePlan(DependencyScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (Document is null)
        {
            return null;
        }

        string levelFile = LevelFileName();
        string installDir = _session.RfInstallDir ?? Environment.CurrentDirectory;
        string outPath = PackfileBuildPlan.DefaultOutputPath(installDir, levelFile, IsMultiplayerLevel());
        return new PackfileBuildPlan(scan, levelFile, outPath);
    }

    /// <summary>Opens the Create-Level-Packfile dialog pre-populated with <paramref name="plan"/>.</summary>
    public async Task OpenPackfileAsync(PackfileBuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Document is not { } doc)
        {
            return;
        }

        byte[] levelBytes = doc.SaveToBytes(updateTimestamp: true);
        PackfileBuildResult? result = await Dialogs.PackfileDialog.ShowAsync(this, plan, levelBytes);
        if (result is null)
        {
            _dispatcher.ShowMessage("Packfile cancelled.");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Wrote {result.OutputPath}");
        sb.AppendLine($"  {result.PackedFiles.Count} file(s), {result.TotalBytes:N0} bytes (before alignment).");
        if (result.SkippedUnreadable.Count > 0)
        {
            sb.AppendLine($"  Skipped (unreadable): {string.Join(", ", result.SkippedUnreadable)}");
        }

        if (result.SkippedNameTooLong.Count > 0)
        {
            sb.AppendLine($"  Skipped (name too long for VPP): {string.Join(", ", result.SkippedNameTooLong)}");
        }

        SetBuildOutput("Packfile", sb.ToString());
        _dispatcher.ShowMessage($"Packed {result.PackedFiles.Count} file(s) → {System.IO.Path.GetFileName(result.OutputPath)}");
    }

    private async Task OpenPackfileCommandAsync()
    {
        if (Document is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        if (_session.Vfs is null)
        {
            _dispatcher.ShowMessage("Mount an RF install before packing (Settings → install path).");
            return;
        }

        _dispatcher.ShowMessage("Scanning level dependencies…");
        DependencyScanResult? scan = await ScanDependenciesAsync();
        if (scan is null || CreatePackfilePlan(scan) is not { } plan)
        {
            _dispatcher.ShowMessage("Could not scan dependencies.");
            return;
        }

        await OpenPackfileAsync(plan);
    }

    private string LevelFileName()
    {
        if (_session.LevelPath is { } path)
        {
            return System.IO.Path.GetFileName(path);
        }

        string name = Document?.Rfl.Header.LevelName ?? "level.rfl";
        return name.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase) ? name : name + ".rfl";
    }

    private bool IsMultiplayerLevel() =>
        Document?.Rfl.Sections
            .Select(s => s.Content)
            .OfType<LevelInfoSection>()
            .FirstOrDefault()?.MultiplayerLevel != 0;
}

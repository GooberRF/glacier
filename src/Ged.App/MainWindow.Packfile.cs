using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ged.Core.Input;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Packaging;

namespace Ged.App;

/// <summary>
/// File &gt; Create Level Packfile writes <c>&lt;level folder&gt;\&lt;level name&gt;.vpp</c> (replacing it in place) with
/// the saved level file first and its custom dependencies after it, asking nothing unless a dependency is missing;
/// File &gt; Create Level Packfile As… always opens the review dialog, with a path picker. Both pack the level as it is
/// now (<see cref="PackfileCommand"/>). The scan / plan / dialog steps are exposed on <see cref="Panels.IEditorHost"/> so
/// the Dependency Graph panel can drive them with its own graph include state.
/// </summary>
public sealed partial class MainWindow
{
    private void InitPackfile()
    {
        _dispatcher.Bind(CommandIds.FilePackfile, () => _ = CreatePackfileAsync(review: false), () => Document is not null);
        _dispatcher.Bind(CommandIds.FilePackfileAs, () => _ = CreatePackfileAsync(review: true), () => Document is not null);
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

    /// <summary>
    /// Builds a packfile plan from a scan: the level's own <c>&lt;level folder&gt;\&lt;level name&gt;.vpp</c>
    /// (<see cref="PackfileBuildPlan.LevelFolderOutputPath"/>), or, for a never-saved level, the install's user-maps
    /// location. Null without a document.
    /// </summary>
    public PackfileBuildPlan? CreatePackfilePlan(DependencyScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (Document is null)
        {
            return null;
        }

        string levelFile = LevelFileName();
        string outPath = Document.Path is { } saved
            ? PackfileBuildPlan.LevelFolderOutputPath(saved)
            : PackfileBuildPlan.DefaultOutputPath(_session.RfInstallDir ?? Environment.CurrentDirectory, levelFile, IsMultiplayerLevel());
        return new PackfileBuildPlan(scan, levelFile, outPath);
    }

    /// <summary>
    /// Opens the Create-Level-Packfile review dialog with <paramref name="plan"/> (the Dependency Graph panel's include
    /// state). The level is saved first when it has unsaved changes, as for the menu commands.
    /// </summary>
    public async Task OpenPackfileAsync(PackfileBuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await RunPackfileAsync(review: true, _ => plan);
    }

    /// <summary>Create Level Packfile (<paramref name="review"/> false) and Create Level Packfile As… (true).</summary>
    private async Task CreatePackfileAsync(bool review)
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

        await RunPackfileAsync(review, CreatePackfilePlan);
    }

    private async Task RunPackfileAsync(bool review, Func<DependencyScanResult, PackfileBuildPlan?> plan)
    {
        if (Document is not { } doc)
        {
            return;
        }

        PackfileCommandOutcome outcome;
        try
        {
            outcome = await PackfileCommand.RunAsync(
                doc,
                _buildController?.GeometryIsPreview == true,
                review,
                async path => (await SaveToPathAsync(path, saveAs: false, packaging: true)).Written,
                async () =>
                {
                    _dispatcher.ShowMessage("Scanning level dependencies…");
                    return await ScanDependenciesAsync();
                },
                plan,
                (p, bytes) =>
                {
                    if (!p.CanBuild)
                    {
                        LogOperation("Packfile", $"{p.Scan.Missing.Count} dependency file(s) are missing: review them before packing.");
                    }

                    return Dialogs.PackfileDialog.ShowAsync(this, p, bytes);
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _notifications.Notify(Services.NotificationSeverity.Error, $"Packfile not written: {ex.Message}");
            return;
        }

        if (outcome.SavedFirst && doc.Path is { } saved)
        {
            LogOperation("Packfile", $"Saved {Path.GetFileName(saved)} first, so the packfile carries the level as it is now.");
        }

        if (outcome.Refusal is { } refusal)
        {
            _notifications.Notify(Services.NotificationSeverity.Warning, refusal);
            return;
        }

        ReportPackfile(outcome.Result);
    }

    private void ReportPackfile(PackfileBuildResult? result)
    {
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
        _dispatcher.ShowMessage($"Packed {result.PackedFiles.Count} file(s) → {Path.GetFileName(result.OutputPath)}");
    }

    private string LevelFileName()
    {
        if (_session.LevelPath is { } path)
        {
            return Path.GetFileName(path);
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

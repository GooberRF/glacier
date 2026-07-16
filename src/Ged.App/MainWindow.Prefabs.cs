using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Ged.Core.Editing;
using Ged.Core.Input;
using Ged.Core.IO.Rfg;
using Ged.Core.Model;
using Ged.Core.Prefabs;

namespace Ged.App;

/// <summary>
/// Prefabs: Save Selection As Prefab (renders a thumbnail via the
/// offscreen path, bundles payload.rfg + manifest into a .gedprefab zip) and the
/// Asset Browser Prefabs tab (scans the prefab dirs, thumbnails, search, place =
/// .rfg import with UID remap at the camera).
/// </summary>
public sealed partial class MainWindow
{
    private TextBox? _prefabFilter;
    private WrapPanel? _prefabGrid;

    private void InitPrefabs()
    {
        _dispatcher.Bind(CommandIds.FileSaveAsPrefab, () => _ = SaveSelectionAsPrefabAsync(), () => HasSelectionForPrefab());
    }

    private bool HasSelectionForPrefab() =>
        (BrushEd?.SelectedBrushes.Count ?? 0) > 0 || (Document?.Selection.Count ?? 0) > 0;

    // ---- Save Selection As Prefab ---------------------------------------------

    private async Task SaveSelectionAsPrefabAsync()
    {
        if (Document is null || !HasSelectionForPrefab())
        {
            _dispatcher.ShowMessage("Select brushes and/or objects to save as a prefab.");
            return;
        }

        string? name = await Dialogs.InputDialog.ShowAsync(this, "Save Prefab", "Prefab name:", "prefab");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var brushUids = BrushEd?.SelectedBrushes.ToList() ?? new List<int>();
            var objectUids = Document.Selection.Select(o => o.Uid).ToList();
            RfgFile rfg = RfgInterop.Export(Document, brushUids, objectUids, alpine: true, groupName: name);

            byte[]? thumb = RenderPrefabThumbnail(brushUids);

            var manifest = new PrefabManifest
            {
                Name = name,
                Author = Environment.UserName,
                BrushCount = brushUids.Count,
                ObjectCount = objectUids.Count,
            };

            string dir = PrimaryPrefabDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, SanitizeFileName(name) + PrefabPackage.Extension);
            PrefabPackage.Save(path, manifest, rfg, thumb);

            RebuildPrefabGrid();
            _dispatcher.ShowMessage($"Saved prefab '{name}' ({brushUids.Count} brush(es), {objectUids.Count} object(s)) → {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Save prefab failed: {ex.Message}");
        }
    }

    private byte[]? RenderPrefabThumbnail(IReadOnlyList<int> brushUids)
    {
        try
        {
            var brushes = brushUids.Select(u => BrushEd?.FindBrush(u)).Where(b => b is not null).Cast<Ged.Core.Model.Brush>().ToList();
            if (brushes.Count == 0)
            {
                return null;
            }

            Ged.Rendering.Graphics.GraphicsDevice? dev = TryGetDevice();
            return dev is null ? null : Ged.Rendering.PrefabThumbnail.Render(dev, _session.Vfs, brushes, size: 128);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Prefabs browser tab --------------------------------------------------

    private Control BuildPrefabsTab()
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(6) };
        var top = new StackPanel { Spacing = 4 };
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);

        _prefabFilter = new TextBox { Watermark = "search prefabs…", FontSize = 12 };
        _prefabFilter.TextChanged += (_, _) => RebuildPrefabGrid();
        top.Children.Add(_prefabFilter);
        top.Children.Add(Row(
            Btn("Save Selection…", () => _dispatcher.Invoke(CommandIds.FileSaveAsPrefab)),
            Btn("Update from Selection…", () => _ = UpdatePrefabFromSelectionAsync()),
            Btn("Refresh", RebuildPrefabGrid),
            Btn("Open Folder", OpenPrefabFolder)));
        top.Children.Add(Note("Double-click a prefab to place a tracked instance. Update from Selection overwrites a prefab and propagates to its placed instances."));
        root.Children.Add(top);

        _prefabGrid = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(new ScrollViewer { Content = _prefabGrid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        RebuildPrefabGrid();
        return root;
    }

    private void RebuildPrefabGrid()
    {
        if (_prefabGrid is null)
        {
            return;
        }

        _prefabGrid.Children.Clear();
        string filter = _prefabFilter?.Text?.Trim() ?? string.Empty;

        foreach (string path in EnumeratePrefabs())
        {
            PrefabManifest manifest;
            byte[]? thumb;
            try
            {
                (manifest, thumb) = PrefabPackage.LoadHeader(path);
            }
            catch (Exception)
            {
                continue; // skip a corrupt package
            }

            string display = string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileNameWithoutExtension(path) : manifest.Name;
            if (filter.Length > 0 && !display.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _prefabGrid.Children.Add(BuildPrefabTile(path, display, manifest, thumb));
        }

        if (_prefabGrid.Children.Count == 0)
        {
            _prefabGrid.Children.Add(new TextBlock
            {
                Text = "(no prefabs — select geometry and Save Selection…)",
                Foreground = Brushes.Gray,
                Margin = new Avalonia.Thickness(6),
            });
        }
    }

    private Control BuildPrefabTile(string path, string display, PrefabManifest manifest, byte[]? thumb)
    {
        var img = new Image { Width = 72, Height = 72, Stretch = Stretch.Uniform };
        if (thumb is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(thumb);
                img.Source = new Bitmap(ms);
            }
            catch (Exception)
            {
                // leave blank
            }
        }

        string tip = $"{display}\n{manifest.BrushCount} brush(es), {manifest.ObjectCount} object(s)\n{manifest.Created:yyyy-MM-dd}";
        var btn = new Button
        {
            Margin = new Avalonia.Thickness(2),
            Padding = new Avalonia.Thickness(2),
            [ToolTip.TipProperty] = tip,
            Content = new StackPanel
            {
                Children =
                {
                    img,
                    new TextBlock { Text = Shorten(display), FontSize = 9, MaxWidth = 72, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
        btn.DoubleTapped += (_, _) => PlacePrefab(path);
        return btn;
    }

    private void PlacePrefab(string path)
    {
        if (Document is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        try
        {
            PrefabPackage pkg = PrefabPackage.Load(path);
            Vec3 at = PlacementPoint;
            string name = string.IsNullOrWhiteSpace(pkg.Manifest.Name) ? Path.GetFileNameWithoutExtension(path) : pkg.Manifest.Name;
            IReadOnlyList<int> placed;

            // Record a tracked instance (import + lineage record are ONE undo entry — item 1).
            using (Document.Undo.BeginTransaction($"Place prefab '{name}'"))
            {
                placed = RfgInterop.Import(Document, pkg.Payload, at);
                _prefabInstances?.RecordInstance(name, PrefabHash(path), placed, at, Ged.Core.Model.Mat3.Identity);
            }

            AfterMutation();
            _linkGraph.Refresh();
            _outliner.Refresh();
            _dispatcher.ShowMessage($"Placed prefab instance '{name}' — {placed.Count} object(s) at the camera.");
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Place prefab failed: {ex.Message}");
        }
    }

    /// <summary>Orphans a prefab instance (IEditorHost): the lineage record goes, members stay.</summary>
    public void OrphanPrefabInstance(int instanceId)
    {
        if (_prefabInstances?.Orphan(instanceId) == true)
        {
            AfterMutation();
            _dispatcher.ShowMessage("Instance orphaned — members are now plain content.");
        }
    }

    /// <summary>Selects every member (brushes + objects) of a prefab instance (IEditorHost).</summary>
    public void SelectPrefabInstanceMembers(int instanceId)
    {
        if (Document is null || _prefabInstances?.ById(instanceId) is not { } rec)
        {
            return;
        }

        Document.ClearSelection();
        BrushEd?.ClearSelection();
        foreach (int uid in rec.MemberUids)
        {
            if (Document.FindByUid(uid) is { } o)
            {
                _session.Selection.SelectObject(o, additive: true);
            }
            else if (BrushEd?.FindBrush(uid) is not null)
            {
                _session.Selection.SelectBrush(uid, additive: true);
            }
        }

        RefreshSelectionOverlay();
        _dispatcher.ShowMessage($"Selected {rec.MemberUids.Count} instance member(s).");
    }

    /// <summary>
    /// "Update Prefab from Selection": overwrites an existing .gedprefab with the current
    /// selection, then offers to propagate the change to every non-orphaned instance of it
    /// (item 1). Modified instances are included only when the user confirms the force prompt.
    /// </summary>
    private async Task UpdatePrefabFromSelectionAsync()
    {
        if (Document is null || !HasSelectionForPrefab())
        {
            _dispatcher.ShowMessage("Select the updated prefab geometry/objects first.");
            return;
        }

        string existing = string.Join(", ", EnumeratePrefabs()
            .Select(p => { try { return PrefabPackage.LoadHeader(p).Manifest.Name; } catch { return string.Empty; } })
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
        string? name = await Dialogs.InputDialog.ShowAsync(this, "Update Prefab", $"Prefab to overwrite (existing: {existing}):", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var brushUids = BrushEd?.SelectedBrushes.ToList() ?? new List<int>();
            var objectUids = Document.Selection.Select(o => o.Uid).ToList();
            RfgFile rfg = RfgInterop.Export(Document, brushUids, objectUids, alpine: true, groupName: name);

            string dir = PrimaryPrefabDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, SanitizeFileName(name) + PrefabPackage.Extension);
            var manifest = new PrefabManifest { Name = name, Author = Environment.UserName, BrushCount = brushUids.Count, ObjectCount = objectUids.Count };
            PrefabPackage.Save(path, manifest, rfg, RenderPrefabThumbnail(brushUids));
            RebuildPrefabGrid();

            int total = _prefabInstances?.Instances.Count(i => string.Equals(i.PrefabName, name, StringComparison.OrdinalIgnoreCase)) ?? 0;
            int modified = _prefabInstances?.Instances.Count(i => string.Equals(i.PrefabName, name, StringComparison.OrdinalIgnoreCase) && i.Modified) ?? 0;
            if (total == 0)
            {
                _dispatcher.ShowMessage($"Saved prefab '{name}' (no placed instances to propagate).");
                return;
            }

            bool force = false;
            if (modified > 0)
            {
                force = await Dialogs.ConfirmDialog.ShowAsync(this, "Propagate Prefab",
                    $"{total} instance(s) of '{name}' ({modified} locally modified).\n\nForce-propagate to the modified instances too?");
            }

            int done = _prefabInstances?.Propagate(name, rfg, PrefabHash(path), force) ?? 0;
            AfterMutation();
            _linkGraph.Refresh();
            _outliner.Refresh();
            _dispatcher.ShowMessage($"Updated '{name}' → propagated to {done} of {total} instance(s)" + (modified > 0 && !force ? $" ({modified} modified skipped)" : string.Empty));
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Update prefab failed: {ex.Message}");
        }
    }

    private static string PrefabHash(string path)
    {
        try
        {
            using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)))[..16];
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void OpenPrefabFolder()
    {
        string dir = PrimaryPrefabDir();
        Directory.CreateDirectory(dir);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Could not open {dir}: {ex.Message}");
        }
    }

    // ---- Prefab directories ---------------------------------------------------

    private string PrimaryPrefabDir()
    {
        if (!string.IsNullOrWhiteSpace(_settings.PrefabDirectory))
        {
            return _settings.PrefabDirectory;
        }

        return Ged.Core.AppPaths.DefaultPrefabsDirectory;
    }

    private IEnumerable<string> EnumeratePrefabs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in PrefabDirs())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string f in Directory.EnumerateFiles(dir, "*" + PrefabPackage.Extension))
            {
                if (seen.Add(Path.GetFullPath(f)))
                {
                    yield return f;
                }
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[name.Length];
        int n = 0;
        foreach (char c in name)
        {
            buffer[n++] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }

        string s = new string(buffer[..n]).Trim();
        return string.IsNullOrEmpty(s) ? "prefab" : s;
    }

    private IEnumerable<string> PrefabDirs()
    {
        yield return PrimaryPrefabDir();

        // A project-relative "prefabs" directory next to the open level.
        if (Document?.Path is { } lp && Path.GetDirectoryName(lp) is { } levelDir)
        {
            yield return Path.Combine(levelDir, "prefabs");
        }
    }
}

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
using Ged.Core.Editor;
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

            // Store the payload in FIXED prefab-local space: re-base so its origin IS the pivot (bbox
            // centre at save time). Placement/propagation then never derive a pivot from content.
            RfgInterop.TransformInPlace(rfg, Ged.Core.Model.Mat3.Identity, RfgInterop.ComputePivot(rfg).Scale(-1f));

            byte[]? thumb = RenderPrefabThumbnail(brushUids);

            var manifest = new PrefabManifest
            {
                Name = name,
                Author = Environment.UserName,
                BrushCount = brushUids.Count,
                ObjectCount = objectUids.Count,
                PivotBased = true,
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
        WireHoverPreview(btn, preview =>
        {
            if (thumb is { Length: > 0 })
            {
                try
                {
                    using var ms = new MemoryStream(thumb);
                    preview.Source = new Bitmap(ms);
                }
                catch (Exception)
                {
                    // leave blank
                }
            }
        });
        WirePlaceableDrag(btn, PlaceableDrag.Prefab(path));
        return btn;
    }

    private void PlacePrefab(string path) => PlacePrefabAt(path, PlacementPoint, "at the camera");

    /// <summary>
    /// Places a tracked prefab instance so its pivot lands at <paramref name="pivotPosition"/>
    /// (double-click uses the in-front-of-camera <see cref="PlacementPoint"/>, matching a mesh
    /// object; a viewport drop uses the face-hit / camera-fallback point). The prefab is RE-CENTERED
    /// on its own pivot before offsetting, so it appears at the target point rather than at its
    /// authored world origin plus the offset. Import + lineage record are ONE undo transaction.
    /// </summary>
    private void PlacePrefabAt(string path, Vec3 pivotPosition, string whereNote)
    {
        if (Document is null || _prefabInstances is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        try
        {
            PrefabPackage pkg = PrefabPackage.Load(path);
            string name = string.IsNullOrWhiteSpace(pkg.Manifest.Name) ? Path.GetFileNameWithoutExtension(path) : pkg.Manifest.Name;
            RfgFile payload = BasedPayload(pkg);

            PrefabInstanceRecord rec = _prefabInstances.PlaceInstance(
                payload, name, PrefabHash(path), pivotPosition, Ged.Core.Model.Mat3.Identity);

            InvalidatePrefabBrushGeometry(payload); // imported brushes bypass BrushEditor — invalidate CSG
            AfterMutation();
            _linkGraph.Refresh();
            _outliner.Refresh();
            _dispatcher.ShowMessage($"Placed prefab instance '{name}' — {rec.MemberUids.Count} object(s) {whereNote}.");
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
    /// "Update Prefab from Selection" (item C1 — no free-text typing): if the selection contains
    /// members of exactly ONE tracked instance, that prefab is updated directly after a single
    /// confirm; otherwise a PICKER of existing prefabs is shown. Either way the chosen prefab's
    /// .gedprefab is overwritten with the selection and the change propagates to its placed
    /// instances (each kept at its own moved/rotated pose). Free-text naming lives only in Save
    /// Selection As Prefab.
    /// </summary>
    private async Task UpdatePrefabFromSelectionAsync()
    {
        if (Document is null || _prefabInstances is null || !HasSelectionForPrefab())
        {
            _dispatcher.ShowMessage("Select the updated prefab geometry/objects first.");
            return;
        }

        // Which tracked instance(s) does the current selection belong to?
        var selected = new HashSet<int>(SelectedMemberUids());
        var distinctNames = _prefabInstances.Instances
            .Where(r => r.MemberUids.Any(selected.Contains))
            .Select(r => r.PrefabName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? name;
        if (distinctNames.Count == 1)
        {
            // Update THAT prefab directly — no typing; a single confirm.
            name = distinctNames[0];
            int total = _prefabInstances.Instances.Count(i => string.Equals(i.PrefabName, name, StringComparison.OrdinalIgnoreCase));
            int others = Math.Max(0, total - 1);
            bool ok = await Dialogs.ConfirmDialog.ShowAsync(this, "Update Prefab",
                $"Update '{name}' from this instance and propagate to {others} other instance(s)?");
            if (!ok)
            {
                return;
            }
        }
        else
        {
            // Selection maps to no (or several) tracked instances → pick an existing prefab (a list, not free text).
            var choices = EnumeratePrefabs()
                .Select(p => { try { return PrefabPackage.LoadHeader(p).Manifest.Name; } catch { return null; } })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (choices.Count == 0)
            {
                _dispatcher.ShowMessage("No existing prefabs to update — use Save Selection… to create one.");
                return;
            }

            name = await Dialogs.PickerDialog.ShowAsync(this, "Update Prefab",
                "Overwrite which prefab with the current selection?", choices);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
        }

        // The SOURCE instance (if the selection belongs to one of this prefab's instances) fixes the
        // prefab-local frame: capturing the new content through its pose keeps every untouched member's
        // local coords byte-identical, so nothing shifts across propagation (defect 1).
        PrefabInstanceRecord? source = _prefabInstances.Instances.FirstOrDefault(r =>
            string.Equals(r.PrefabName, name, StringComparison.OrdinalIgnoreCase) && r.MemberUids.Any(selected.Contains));
        await OverwriteAndPropagateAsync(name, source);
    }

    /// <summary>The UIDs of the current selection (brush + object members), for instance detection.</summary>
    private IEnumerable<int> SelectedMemberUids()
    {
        foreach (int uid in BrushEd?.SelectedBrushes ?? Enumerable.Empty<int>())
        {
            yield return uid;
        }

        foreach (LevelObject o in Document?.Selection ?? Enumerable.Empty<LevelObject>())
        {
            yield return o.Uid;
        }
    }

    /// <summary>
    /// Overwrites <paramref name="name"/>'s .gedprefab with the current selection and propagates
    /// to its placed instances. The exported selection is re-based into FIXED prefab-local space —
    /// through <paramref name="source"/>'s pose when the selection is one of this prefab's instances
    /// (so untouched members keep byte-identical local coords and never shift), else by its bbox
    /// centre. Modified instances are force-propagated only after a confirm.
    /// </summary>
    private async Task OverwriteAndPropagateAsync(string name, PrefabInstanceRecord? source)
    {
        if (Document is null || _prefabInstances is null)
        {
            return;
        }

        try
        {
            var brushUids = BrushEd?.SelectedBrushes.ToList() ?? new List<int>();
            var objectUids = Document.Selection.Select(o => o.Uid).ToList();
            RfgFile rfg = RfgInterop.Export(Document, brushUids, objectUids, alpine: true, groupName: name);

            // Re-base into fixed prefab-local space (defect 1).
            if (source is not null)
            {
                Ged.Core.Model.Mat3 rInv = source.PivotRotation.Transpose();
                RfgInterop.TransformInPlace(rfg, rInv, rInv.Transform(source.PivotPosition).Scale(-1f)); // local = Rᵀ·(world − pivotPos)
            }
            else
            {
                RfgInterop.TransformInPlace(rfg, Ged.Core.Model.Mat3.Identity, RfgInterop.ComputePivot(rfg).Scale(-1f));
            }

            string dir = PrimaryPrefabDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, SanitizeFileName(name) + PrefabPackage.Extension);
            var manifest = new PrefabManifest { Name = name, Author = Environment.UserName, BrushCount = brushUids.Count, ObjectCount = objectUids.Count, PivotBased = true };
            PrefabPackage.Save(path, manifest, rfg, RenderPrefabThumbnail(brushUids));
            RebuildPrefabGrid();

            int total = _prefabInstances.Instances.Count(i => string.Equals(i.PrefabName, name, StringComparison.OrdinalIgnoreCase));
            int modified = _prefabInstances.Instances.Count(i => string.Equals(i.PrefabName, name, StringComparison.OrdinalIgnoreCase) && i.Modified);
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

            int done = _prefabInstances.Propagate(name, rfg, PrefabHash(path), force);
            InvalidatePrefabBrushGeometry(rfg); // propagation deleted/re-imported brushes outside BrushEditor (defect 2)
            _prefabUnit?.ValidateExisting();     // propagated instances were re-created with fresh UIDs
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

    /// <summary>The (possibly legacy) package payload normalized to FIXED prefab-local space (origin == pivot).</summary>
    private static RfgFile BasedPayload(PrefabPackage pkg)
    {
        RfgFile payload = pkg.Payload;
        if (!pkg.Manifest.PivotBased)
        {
            // Legacy v1 package: establish the pivot once (bbox centre) and re-base in memory, then
            // treat it as fixed for this placement (no per-propagation re-derivation).
            RfgInterop.TransformInPlace(payload, Ged.Core.Model.Mat3.Identity, RfgInterop.ComputePivot(payload).Scale(-1f));
        }

        return payload;
    }

    /// <summary>
    /// After a prefab placement/propagation that imported brushes (which bypass <c>BrushEditor</c>,
    /// so its <c>BrushesChanged</c> invalidation never fired), invalidates the compiled geometry
    /// exactly like a structural brush edit: marks it dirty, drops the merged-brush stash wholesale,
    /// and kicks the live-CSG preview — no-op when the payload carried no brushes (defect 2).
    /// </summary>
    private void InvalidatePrefabBrushGeometry(RfgFile payload)
    {
        if (payload.Groups.Any(g => g.Brushes.Brushes.Count > 0))
        {
            _buildController?.InvalidateBrushGeometry();
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

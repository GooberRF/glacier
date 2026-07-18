using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ged.Core.Assets;
using Ged.Core.Input;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Tex;
using Ged.Core.Packaging;
using Ged.Rendering;
using Ged.Rendering.Graphics;

namespace Ged.App;

/// <summary>
/// The dockable Asset Browser: a Textures / Meshes / Sounds tabbed library
/// over the full VFS. Textures get an async thumbnail grid (category + favorites,
/// instant filter, tile-size slider, show-tiled + show-only-used, apply-to-
/// selection, info tooltip, where-used); Meshes get rendered thumbnails through
/// the offscreen GPU path (cached, CPU-raster fallback) + click-to-place; Sounds
/// get name + winmm preview. Reload / Library Health / Verify wired to Tools.
/// </summary>
public sealed partial class MainWindow
{
    private const string FavoritesCategory = "★ Favorites";

    private ComboBox? _abTexCategory;
    private TextBox? _abTexFilter;
    private CheckBox? _abTexOnlyUsed;
    private CheckBox? _abTexTiled;
    private Slider? _abTexTileSize;
    private WrapPanel? _abTexGrid;
    private TextBlock? _abTexInfo;
    private TextBlock? _abTexCurrent;

    private TextBox? _abMeshFilter;
    private WrapPanel? _abMeshGrid;
    private TextBlock? _abMeshInfo;

    private TextBox? _abSoundFilter;
    private ListBox? _abSoundList;

    private readonly ThumbnailCache _meshThumbs = new(Path.Combine(ThumbnailCache.DefaultCacheDirectory(), "mesh"));

    /// <summary>Shared large-preview popover for the Asset Browser tiles (item D).</summary>
    private readonly Panels.AssetHoverPreview _assetPreview = new();

    private void InitAssetBrowser()
    {
        _dispatcher.Bind(CommandIds.ToolsReloadTextures, ReloadTextures);
        _dispatcher.Bind(CommandIds.ToolsReloadMeshes, ReloadMeshes);
        _dispatcher.Bind(CommandIds.ToolsLibraryHealth, RunLibraryHealth);
        _dispatcher.Bind(CommandIds.ToolsVerifyTextures, RunVerifyTextures);
    }

    private Control BuildAssetBrowserPanel()
    {
        var tabs = new TabControl { Margin = new Avalonia.Thickness(2) };
        tabs.Items.Add(new TabItem { Header = "Textures", Content = BuildTexturesTab() });
        tabs.Items.Add(new TabItem { Header = "Meshes", Content = BuildMeshesTab() });
        tabs.Items.Add(new TabItem { Header = "Sounds", Content = BuildSoundsTab() });
        tabs.Items.Add(new TabItem { Header = "Prefabs", Content = BuildPrefabsTab() });
        tabs.SelectionChanged += (_, _) => _assetPreview.Cancel(); // no stale popover across tab switches

        // Host the shared hover-preview popover in the panel tree (a Popup needs a visual parent to
        // open); it takes no layout space.
        var host = new Panel();
        host.Children.Add(tabs);
        host.Children.Add(_assetPreview.Popup);
        return host;
    }

    /// <summary>Wires the large hover preview onto a tile: dwell-open, snap between tiles, close on leave.</summary>
    private void WireHoverPreview(Control tile, Action<Image> render)
    {
        tile.PointerEntered += (_, _) => _assetPreview.Schedule(tile, render);
        tile.PointerExited += (_, _) => _assetPreview.ScheduleClose(tile);
    }

    // ---- Textures tab ---------------------------------------------------------

    private Control BuildTexturesTab()
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(6) };

        var top = new StackPanel { Spacing = 4 };
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);

        _abTexCategory = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _abTexCategory.SelectionChanged += (_, _) => RebuildAbTextureGrid();
        top.Children.Add(_abTexCategory);

        _abTexFilter = new TextBox { Watermark = "search textures…", FontSize = 12 };
        _abTexFilter.TextChanged += (_, _) => RebuildAbTextureGrid();
        top.Children.Add(_abTexFilter);

        var opts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _abTexOnlyUsed = new CheckBox { Content = "Only Used" };
        _abTexOnlyUsed.IsCheckedChanged += (_, _) => RebuildAbTextureGrid();
        _abTexTiled = new CheckBox { Content = "Show Tiled" };
        _abTexTiled.IsCheckedChanged += (_, _) => RebuildAbTextureGrid();
        opts.Children.Add(_abTexOnlyUsed);
        opts.Children.Add(_abTexTiled);
        top.Children.Add(opts);

        _abTexTileSize = new Slider { Minimum = 32, Maximum = 128, Value = _settings.AssetTileSize, TickFrequency = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        _abTexTileSize.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                _settings.AssetTileSize = (int)_abTexTileSize.Value;
                RebuildAbTextureGrid();
            }
        };
        top.Children.Add(Labeled("Tile size", _abTexTileSize));

        top.Children.Add(Row(
            Btn("Apply to Selection", () => { if (_abCurrentTex is { } t) { _texCurrent = t; ApplyCurrentTexture(); } }),
            Btn("Favorite ★", () => ToggleFavorite(_abCurrentTex)),
            Btn("Where Used", () => WhereUsedFor(_abCurrentTex))));
        top.Children.Add(Row(
            Btn("Reload Textures", ReloadTextures),
            Btn("Library Health", RunLibraryHealth),
            Btn("Verify Textures", RunVerifyTextures)));

        _abTexCurrent = new TextBlock { FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
        top.Children.Add(_abTexCurrent);
        _abTexInfo = new TextBlock { FontSize = 10, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
        top.Children.Add(_abTexInfo);

        root.Children.Add(top);

        _abTexGrid = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(new ScrollViewer { Content = _abTexGrid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        PopulateAbCategories();
        return root;
    }

    private string? _abCurrentTex;

    private void PopulateAbCategories()
    {
        if (_abTexCategory is null)
        {
            return;
        }

        var names = new List<string> { FavoritesCategory };
        if (_session.Vfs is { } vfs)
        {
            names.AddRange(vfs.GetTextureCategories().Select(c => c.ToString()));
        }

        if (names.Count == 1)
        {
            names.Add("(mount an RF install for textures)");
        }

        _abTexCategory.ItemsSource = names;
        _abTexCategory.SelectedIndex = names.FindIndex(n => n.StartsWith("All", StringComparison.Ordinal)) is int i && i > 0 ? i : 0;
        RebuildAbTextureGrid();
    }

    private IReadOnlyList<string> AbCategoryFiles()
    {
        if (_session.Vfs is not { } vfs || _abTexCategory?.SelectedItem is not string sel)
        {
            return Array.Empty<string>();
        }

        if (sel == FavoritesCategory)
        {
            return _settings.TextureFavorites;
        }

        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();
        AssetCategory? cat = cats.FirstOrDefault(c => c.ToString() == sel);
        return cat?.Files ?? Array.Empty<string>();
    }

    private void RebuildAbTextureGrid()
    {
        if (_abTexGrid is null)
        {
            return;
        }

        _abTexGrid.Children.Clear();
        IEnumerable<string> files = AbCategoryFiles();

        string filter = _abTexFilter?.Text?.Trim() ?? string.Empty;
        if (filter.Length > 0)
        {
            files = files.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        if (_abTexOnlyUsed?.IsChecked == true && _session.Document is { } doc)
        {
            var used = WhereUsed.UsedTextureBaseNames(doc.Rfl);
            files = files.Where(f => used.Contains(SupercedeChain.GetBaseName(f)));
        }

        var list = files.Take(400).ToList();
        foreach (string name in list)
        {
            _abTexGrid.Children.Add(BuildAbTextureTile(name));
        }

        if (list.Count == 0)
        {
            _abTexGrid.Children.Add(new TextBlock { Text = "(no textures)", Foreground = Brushes.Gray, Margin = new Avalonia.Thickness(6) });
        }
    }

    private Control BuildAbTextureTile(string name)
    {
        int size = Math.Clamp(_settings.AssetTileSize, 32, 128);
        bool fav = _settings.TextureFavorites.Contains(name, StringComparer.OrdinalIgnoreCase);
        var img = new Image { Width = size, Height = size, Stretch = Stretch.Fill };
        var caption = new TextBlock { Text = (fav ? "★ " : string.Empty) + Shorten(name), FontSize = 8, MaxWidth = size, TextTrimming = TextTrimming.CharacterEllipsis };
        var btn = new Button
        {
            Margin = new Avalonia.Thickness(2),
            Padding = new Avalonia.Thickness(2),
            [ToolTip.TipProperty] = name,
            Content = new StackPanel { Children = { img, caption } },
        };
        btn.Click += (_, _) => SelectAbTexture(name);
        btn.DoubleTapped += (_, _) => { SelectAbTexture(name); _texCurrent = name; ApplyCurrentTexture(); };
        LoadAbTextureThumb(img, name, size);
        WireHoverPreview(btn, preview => LoadAbTextureThumb(preview, name, Panels.AssetHoverPreview.PreviewSize));
        WirePlaceableDrag(btn, PlaceableDrag.Texture(name)); // drag onto a face to apply it (item 3)
        return btn;
    }

    private void SelectAbTexture(string name)
    {
        _abCurrentTex = name;
        _texCurrent = name;
        if (_abTexCurrent is not null)
        {
            _abTexCurrent.Text = "Current: " + name;
        }

        // Info tooltip: dims + source mount + loose/vpp path.
        string info = name;
        (int w, int h) = TexDims(name);
        info = $"{w}x{h}  {Path.GetExtension(name).TrimStart('.').ToUpperInvariant()}";
        if (_session.Vfs?.Locate(name) is { } loc)
        {
            info += $"\nSource: {loc.Origin}  ({loc.Size} bytes)";
        }

        if (_abTexInfo is not null)
        {
            _abTexInfo.Text = info;
        }
    }

    private async void LoadAbTextureThumb(Image img, string name, int size)
    {
        if (_session.Vfs is not { } vfs)
        {
            return;
        }

        bool tiled = _abTexTiled?.IsChecked == true;
        try
        {
            byte[]? png = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    return _thumbs.GetOrCreate($"{name}|tile{(tiled ? 2 : 1)}", "vfs1", () =>
                    {
                        DecodedTexture? d = vfs.LoadTexture(name);
                        TextureImage src = d?.Primary ?? new TextureImage(1, 1, new byte[4]);
                        return tiled ? Tile2x2(src) : src;
                    });
                }
                catch (Exception)
                {
                    return null;
                }
            });

            if (png is not null)
            {
                using var ms = new MemoryStream(png);
                img.Source = new Bitmap(ms);
            }
        }
        catch (Exception)
        {
            // Non-fatal.
        }
    }

    private static TextureImage Tile2x2(TextureImage src)
    {
        int w = src.Width, h = src.Height;
        var px = new byte[w * 2 * h * 2 * 4];
        for (int ty = 0; ty < 2; ty++)
        {
            for (int tx = 0; tx < 2; tx++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        (byte r, byte g, byte b, byte a) = src.GetPixel(x, y);
                        int dx = tx * w + x, dy = ty * h + y;
                        int o = ((dy * w * 2) + dx) * 4;
                        px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = a;
                    }
                }
            }
        }

        return new TextureImage(w * 2, h * 2, px);
    }

    private void ToggleFavorite(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        if (!_settings.TextureFavorites.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)).Equals(0))
        {
            _dispatcher.ShowMessage($"Removed {name} from favorites.");
        }
        else
        {
            _settings.TextureFavorites.Add(name);
            _dispatcher.ShowMessage($"Favorited {name}.");
        }

        Persist();
        RebuildAbTextureGrid();
    }

    // ---- Meshes tab -----------------------------------------------------------

    private Control BuildMeshesTab()
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(6) };
        var top = new StackPanel { Spacing = 4 };
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);

        _abMeshFilter = new TextBox { Watermark = "search meshes…", FontSize = 12 };
        _abMeshFilter.TextChanged += (_, _) => RebuildAbMeshGrid();
        top.Children.Add(_abMeshFilter);
        top.Children.Add(Row(Btn("Reload Meshes", ReloadMeshes)));
        top.Children.Add(Note("Double-click a mesh to place it as a Mesh Object at the camera."));
        _abMeshInfo = new TextBlock { FontSize = 10, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
        top.Children.Add(_abMeshInfo);
        root.Children.Add(top);

        _abMeshGrid = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(new ScrollViewer { Content = _abMeshGrid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        RebuildAbMeshGrid();
        return root;
    }

    private void RebuildAbMeshGrid()
    {
        if (_abMeshGrid is null)
        {
            return;
        }

        _abMeshGrid.Children.Clear();
        if (_session.Vfs is not { } vfs)
        {
            _abMeshGrid.Children.Add(new TextBlock { Text = "(mount an RF install for meshes)", Foreground = Brushes.Gray, Margin = new Avalonia.Thickness(6) });
            return;
        }

        IEnumerable<string> meshes = vfs.EnumerateMeshes();
        string filter = _abMeshFilter?.Text?.Trim() ?? string.Empty;
        if (filter.Length > 0)
        {
            meshes = meshes.Where(m => m.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        foreach (string name in meshes.Take(200))
        {
            _abMeshGrid.Children.Add(BuildAbMeshTile(name));
        }
    }

    private Control BuildAbMeshTile(string name)
    {
        var img = new Image { Width = 64, Height = 64, Stretch = Stretch.Fill };
        var btn = new Button
        {
            Margin = new Avalonia.Thickness(2),
            Padding = new Avalonia.Thickness(2),
            [ToolTip.TipProperty] = name,
            Content = new StackPanel { Children = { img, new TextBlock { Text = Shorten(name), FontSize = 8, MaxWidth = 64, TextTrimming = TextTrimming.CharacterEllipsis } } },
        };
        btn.Click += (_, _) => ShowMeshInfo(name);
        btn.DoubleTapped += (_, _) => PlaceFromPalette(Ged.Core.Editor.LevelObjectKind.MeshObject, name);
        LoadMeshThumb(img, name);
        WireHoverPreview(btn, preview => LoadMeshThumb(preview, name, Panels.AssetHoverPreview.PreviewSize));
        WirePlaceableDrag(btn, PlaceableDrag.Mesh(name));
        return btn;
    }

    private void ShowMeshInfo(string name)
    {
        if (_abMeshInfo is null || _session.Vfs is not { } vfs)
        {
            return;
        }

        try
        {
            V3dFile? mesh = vfs.LoadMesh(name);
            if (mesh is null)
            {
                _abMeshInfo.Text = name + " (not found)";
                return;
            }

            int verts = mesh.Submeshes.SelectMany(s => s.Lods.Take(1)).SelectMany(l => l.Batches).Sum(b => b.NumVertices);
            int materials = mesh.Submeshes.Sum(s => s.Materials.Count);
            string src = vfs.Locate(name)?.Origin ?? "?";
            _abMeshInfo.Text = $"{name}\n{mesh.Submeshes.Count} submesh(es), {verts} verts (LOD0), {materials} material(s)\nSource: {src}";
        }
        catch (Exception ex)
        {
            _abMeshInfo.Text = $"{name}: {ex.Message}";
        }
    }

    private void LoadMeshThumb(Image img, string name, int size = 96)
    {
        if (_session.Vfs is not { } vfs)
        {
            return;
        }

        // Render on the UI thread (shares the process D3D device with the viewport;
        // must not run concurrently on a background thread). Deferred + cached so it
        // trickles in without freezing the grid. The thumbnail cache is size-keyed, so the
        // large hover preview caches independently of the small tile thumbnail.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                GraphicsDevice? dev = TryGetDevice();
                byte[] png = MeshThumbnailRenderer.GetOrRender(_meshThumbs, dev, vfs, name, name, "v1", size);
                using var ms = new MemoryStream(png);
                img.Source = new Bitmap(ms);
            }
            catch (Exception ex)
            {
                CrashHandler.LogNonFatal($"mesh-thumbnail({name})", ex); // leave the tile blank
            }
        }, DispatcherPriority.Background);
    }

    private static GraphicsDevice? TryGetDevice()
    {
        try
        {
            return GpuHost.Device;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Resolves an entity/clutter/item class name to its mesh file via the catalogs.</summary>
    private string? ResolveClassMesh(Ged.Core.Editor.LevelObjectKind kind, string className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        string? mesh = kind switch
        {
            Ged.Core.Editor.LevelObjectKind.Entity => _session.Entities?.Find(className)?.V3dFilename,
            Ged.Core.Editor.LevelObjectKind.Clutter => _session.Clutter?.Find(className)?.V3dFilename,
            Ged.Core.Editor.LevelObjectKind.Item => _session.Items?.Find(className)?.V3dFilename,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(mesh) ? null : mesh;
    }

    /// <summary>
    /// Loads a cache-backed mesh thumbnail for a palette class row (item 1d). Resolves the
    /// class to its mesh via the catalogs and renders on the UI thread (shared device, same
    /// trickle-in pattern as the Asset Browser). Falls back to the kind's viewport icon when
    /// the class has no resolvable mesh / no install is mounted / the render throws, so an Item
    /// row always shows a glyph rather than a blank box.
    /// </summary>
    public void LoadClassThumbnail(Ged.Core.Editor.LevelObjectKind kind, string? className, Image img)
    {
        img.Source = Ged.App.Services.PaletteIcons.TryFor(kind); // fallback glyph until (unless) the mesh renders
        if (className is null || _session.Vfs is not { } vfs)
        {
            return;
        }

        string? mesh = ResolveClassMesh(kind, className);
        if (mesh is null)
        {
            return;
        }

        int size = (int)Math.Max(16, img.Width);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                GraphicsDevice? dev = TryGetDevice();
                byte[] png = MeshThumbnailRenderer.GetOrRender(_meshThumbs, dev, vfs, mesh, mesh, "v1", size);
                using var ms = new MemoryStream(png);
                img.Source = new Bitmap(ms);
            }
            catch (Exception ex)
            {
                CrashHandler.LogNonFatal($"class-thumbnail({className})", ex); // leave the fallback icon in place
            }
        }, DispatcherPriority.Background);
    }

    // ---- Sounds tab -----------------------------------------------------------

    private Control BuildSoundsTab()
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(6) };
        var top = new StackPanel { Spacing = 4 };
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);

        _abSoundFilter = new TextBox { Watermark = "search sounds…", FontSize = 12 };
        _abSoundFilter.TextChanged += (_, _) => RebuildAbSoundList();
        top.Children.Add(_abSoundFilter);
        top.Children.Add(Row(
            Btn("Play", () => { if (_abSoundList?.SelectedItem is string s && !PlaySoundPreview(s)) _dispatcher.ShowMessage("Preview needs a .wav in the VFS."); }),
            Btn("Stop", StopSoundPreview)));
        root.Children.Add(top);

        _abSoundList = new ListBox();
        root.Children.Add(new ScrollViewer { Content = _abSoundList });
        RebuildAbSoundList();
        return root;
    }

    private void RebuildAbSoundList()
    {
        if (_abSoundList is null)
        {
            return;
        }

        var sounds = new List<string>();
        if (_session.Vfs is { } vfs)
        {
            var exts = new[] { ".wav", ".ogg" };
            sounds = vfs.Sources
                .SelectMany(s => s.EnumerateFiles())
                .Where(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        string filter = _abSoundFilter?.Text?.Trim() ?? string.Empty;
        if (filter.Length > 0)
        {
            sounds = sounds.Where(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        _abSoundList.ItemsSource = sounds.Take(2000).ToList();
    }

    // ---- Reload / health / verify --------------------------------------------

    private void ReloadTextures()
    {
        _session.Vfs?.Rescan();
        _thumbs.Clear();
        PopulateAbCategories();
        PopulateCategories();
        _dispatcher.ShowMessage("Textures reloaded.");
    }

    private void ReloadMeshes()
    {
        _session.Vfs?.Rescan();
        _meshThumbs.Clear();
        RebuildAbMeshGrid();
        _dispatcher.ShowMessage("Meshes reloaded.");
    }

    private void WhereUsedFor(string? name)
    {
        if (string.IsNullOrEmpty(name) || _session.Document is not { } doc)
        {
            _dispatcher.ShowMessage("Select a texture and open a level.");
            return;
        }

        var hits = WhereUsed.Find(doc.Rfl, name, _session.BuildScanOptions());
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Where used: {name}  ({hits.Count} hit(s))");
        foreach (AssetUsage u in hits)
        {
            sb.AppendLine($"  [{u.Kind}] {u.Description}");
        }

        SetBuildOutput("Where Used", sb.ToString());
    }

    private void RunLibraryHealth()
    {
        if (_session.Vfs is not { } vfs)
        {
            _dispatcher.ShowMessage("Mount an RF install first.");
            return;
        }

        _dispatcher.ShowMessage("Analyzing library…");
        var texNames = vfs.GetTextureCategories().FirstOrDefault(c => c.Name == "All")?.Files.Take(2000).ToList();
        Services.OperationProgress op = _progress.Begin("Library Health"); // item 3
        op.ReportIndeterminate("analyzing textures…");
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                LibraryHealthReport report = LibraryHealth.Analyze(vfs, texNames);
                Dispatcher.UIThread.Post(() => SetBuildOutput("Library Health", report.ToText()));
            }
            finally
            {
                Dispatcher.UIThread.Post(op.Dispose);
            }
        });
    }

    private void RunVerifyTextures()
    {
        if (_session.Document is not { } doc || _session.Vfs is not { } vfs)
        {
            _dispatcher.ShowMessage("Open a level and mount an RF install first.");
            return;
        }

        _dispatcher.ShowMessage("Verifying textures…");
        Services.OperationProgress op = _progress.Begin("Verify Textures"); // item 3
        op.ReportIndeterminate("checking references…");
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var results = TextureVerifier.Verify(doc.Rfl, vfs);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Verify All Textures — {results.Count} issue(s)");
                foreach (TextureVerifyResult r in results.OrderBy(r => r.Issue))
                {
                    sb.AppendLine($"  [{r.Issue}] {r.TextureName}: {r.Detail}  (used by {r.Usages.Count})");
                }

                if (results.Count == 0)
                {
                    sb.AppendLine("  All referenced textures resolve, power-of-two, within size limits.");
                }

                Dispatcher.UIThread.Post(() => SetBuildOutput("Verify Textures", sb.ToString()));
            }
            finally
            {
                Dispatcher.UIThread.Post(op.Dispose);
            }
        });
    }

    private void SetBuildOutput(string operation, string text) => _logOutput.Append(operation, text);

    /// <summary>Repopulates the browser after an install is mounted or reloaded.</summary>
    private void RefreshAssetBrowser()
    {
        PopulateAbCategories();
        RebuildAbMeshGrid();
        RebuildAbSoundList();
    }
}

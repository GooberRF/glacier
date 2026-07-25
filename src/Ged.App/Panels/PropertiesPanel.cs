using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Input;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;
using Brush = Ged.Core.Model.Brush;

namespace Ged.App.Panels;

/// <summary>
/// A reflection-driven inspector over the current selection. Renders editors for
/// the common object fields (UID read-only, script name, position, hidden) plus
/// the model's simple type-specific fields, with multi-select mixed-value display
/// ("—"). Every edit commits through the document's undo system.
/// </summary>
internal sealed class PropertiesPanel : UserControl
{
    private const string Mixed = "—";

    // A properties inspector with many fields (e.g. a Trigger) overflows the panel, so the
    // ScrollViewer MUST scroll vertically to the very bottom. VerticalScrollBarVisibility is
    // set to Auto EXPLICITLY (a local value): the Fluent/Dock control themes carry a
    // ScrollViewer style, and a styled default silently wins over the built-in default when
    // the property is left unset — that is exactly why the tall inspector could not reach its
    // last field. Horizontal scrolling is disabled so rows wrap to the panel width (the Grid
    // star column stays bounded) rather than growing width and consuming the extent. This
    // mirrors the other tall tool panels (History / Layers / Mode-Tools), which all set these.
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
    };

    private IEditorHost? _host;

    /// <summary>
    /// Optional mesh-file picker for the Mesh-object inspector's Browse button (item 10). The
    /// shell wires this to a StorageProvider picker returning the chosen leaf filename; left null
    /// in headless tests, where the button simply no-ops. Kept off <see cref="IEditorHost"/> to
    /// avoid churning every panel host implementation for one dialog affordance.
    /// </summary>
    public Func<System.Threading.Tasks.Task<string?>>? PickMeshFile { get; set; }

    // Breathing room below the final field. Without it the last editor of a tall inspector
    // (trigger, ~50-field entity) sits flush against the viewport's bottom edge and the
    // horizontal-scroll gutter, so it reads as clipped and is awkward to click. Applied as a
    // bottom MARGIN on the scroll content (a control's margin always counts toward the scroll
    // extent, unlike padding on some themed presenters), it guarantees the last row scrolls
    // fully clear for every inspector routed through SetContent.
    private const double BottomClearance = 40;

    public PropertiesPanel()
    {
        _scroll.Padding = new Avalonia.Thickness(6);
        Content = _scroll;
        ShowEmpty("No selection");
    }

    public void Bind(IEditorHost host) => _host = host;

    /// <summary>Assigns the scroll content, stamping bottom clearance so the final field of a
    /// tall inspector scrolls fully into view instead of being clipped against the bottom edge.</summary>
    private void SetContent(Control content)
    {
        Avalonia.Thickness m = content.Margin;
        content.Margin = new Avalonia.Thickness(m.Left, m.Top, m.Right, m.Bottom + BottomClearance);
        _scroll.Content = content;
    }

    /// <summary>Rebuilds the grid for the current selection.</summary>
    public void Refresh()
    {
        EditorDocument? doc = _host?.Document;
        if (doc is null)
        {
            ShowEmpty("No level open");
            return;
        }

        // Brushes are not LevelObjects — they live in the BrushesSection with their
        // own editor/selection. A non-empty brush selection owns the panel (brush /
        // face / vertex modes clear it when leaving, so the two never fight).
        if (_host?.BrushEditor is { } be && be.SelectedBrushes.Count > 0)
        {
            List<Brush> brushes = be.SelectedBrushes
                .Select(be.FindBrush)
                .Where(b => b is not null)
                .Select(b => b!)
                .OrderBy(b => b.Uid)
                .ToList();
            if (brushes.Count > 0)
            {
                int? uid = brushes.Count == 1 ? brushes[0].Uid : (int?)null;
                SetContent(WrapWithInstanceBanner(uid, BuildBrushInspector(doc, be, brushes)));
                return;
            }
        }

        // Face selection (Face / Texture mode): the shared per-face property editor (item 0f).
        if (_host?.BrushEditor is { } bef && bef.SelectedFaces.Count > 0)
        {
            SetContent(BuildFaceInspector(bef));
            return;
        }

        List<LevelObject> sel = doc.Selection.ToList();
        if (sel.Count == 0)
        {
            ShowEmpty("No selection");
            return;
        }

        // Single selection gets a data-driven inspector: the event catalog for
        // events, the §8 metadata registry for objects, plus the specialized
        // editors (ambient-sound preview, multi-note list, corona colour swatch).
        if (sel.Count == 1)
        {
            int uid = sel[0].Uid;
            if (sel[0].Model is RflEvent ev && EventSchemaCatalog.Find(ev.ClassName) is { } schema)
            {
                SetContent(WrapWithInstanceBanner(uid, BuildEventInspector(doc, sel[0], ev, schema)));
                return;
            }

            switch (sel[0].Model)
            {
                case AmbientSound snd:
                    SetContent(WrapWithInstanceBanner(uid, BuildAmbientSoundInspector(doc, sel[0], snd)));
                    return;
                case AlpineNoteObject note:
                    SetContent(WrapWithInstanceBanner(uid, BuildNoteInspector(doc, sel[0], note)));
                    return;
                case AlpineCoronaObject corona:
                    SetContent(WrapWithInstanceBanner(uid, BuildCoronaInspector(doc, sel[0], corona)));
                    return;
                case AlpineMeshObject mesh:
                    SetContent(WrapWithInstanceBanner(uid, BuildMeshInspector(doc, sel[0], mesh)));
                    return;
                case Light light:
                    SetContent(WrapWithInstanceBanner(uid, BuildLightInspector(doc, sel[0], light)));
                    return;
                case RoomEffect fx:
                    SetContent(WrapWithInstanceBanner(uid, BuildRoomEffectInspector(doc, sel[0], fx)));
                    return;
                case EaxEffect eax:
                    SetContent(WrapWithInstanceBanner(uid, BuildEaxInspector(doc, sel[0], eax)));
                    return;
            }

            if (ObjectInspectorCatalog.For(sel[0].Kind).Count > 0)
            {
                SetContent(WrapWithInstanceBanner(uid, BuildObjectInspector(doc, sel[0])));
                return;
            }
        }

        SetContent(WrapWithInstanceBanner(sel.Count == 1 ? sel[0].Uid : (int?)null, BuildGrid(doc, sel)));
    }

    // ---- Prefab-instance banner (item 1) --------------------------------------

    /// <summary>
    /// Prepends an "instance of X" banner (with Orphan + Select All Members) when the inspected
    /// UID is a member of a placed prefab instance; otherwise returns the inspector unchanged.
    /// </summary>
    private Control WrapWithInstanceBanner(int? uid, Control inner)
    {
        if (uid is int u && _host?.PrefabInstances?.InstanceOfMember(u) is { } rec)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(InstanceBanner(rec));
            stack.Children.Add(inner);
            return stack;
        }

        return inner;
    }

    private Control InstanceBanner(Ged.Core.Model.PrefabInstanceRecord rec)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Instance of '{rec.PrefabName}'" + (rec.Modified ? "   • modified" : string.Empty),
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = rec.Modified ? new SolidColorBrush(Color.FromRgb(255, 190, 90)) : new SolidColorBrush(Color.FromRgb(120, 190, 255)),
            TextWrapping = TextWrapping.Wrap,
        });

        var orphan = new Button { Content = "Orphan", FontSize = 11, Padding = new Avalonia.Thickness(6, 2), [ToolTip.TipProperty] = "Drop the lineage record; members become plain content." };
        orphan.Click += (_, _) => _host?.OrphanPrefabInstance(rec.InstanceId);
        var selectAll = new Button { Content = "Select All Members", FontSize = 11, Padding = new Avalonia.Thickness(6, 2) };
        selectAll.Click += (_, _) => _host?.SelectPrefabInstanceMembers(rec.InstanceId);

        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { orphan, selectAll } });
        return new Border
        {
            Child = panel,
            Padding = new Avalonia.Thickness(8, 6),
            Margin = new Avalonia.Thickness(0, 0, 0, 2),
            Background = new SolidColorBrush(Color.FromArgb(40, 120, 160, 255)),
            CornerRadius = new Avalonia.CornerRadius(4),
        };
    }

    // ---- Data-driven event inspector (from the catalog) -----------------------

    private Control BuildEventInspector(EditorDocument doc, LevelObject lo, RflEvent ev, EventSchema schema)
    {
        var panel = new StackPanel { Spacing = 2 };
        string tags = (schema.IsAlpine ? "  [Alpine ≥300]" : string.Empty) + (schema.HasOrientation ? "  ↻ directional" : string.Empty);
        panel.Children.Add(Header($"{schema.ClassName}  (id {schema.GameId}, {schema.Category}){tags}"));
        if (!string.IsNullOrEmpty(schema.Description))
        {
            panel.Children.Add(new TextBlock { Text = schema.Description, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        panel.Children.Add(ReadonlyRow("UID", lo.Uid.ToString(CultureInfo.InvariantCulture)));
        panel.Children.Add(StringRow("Script Name", new List<LevelObject> { lo }, o => o.ScriptName,
            (o, v) => doc.EditValue(o.Section, "Edit script name", o.ScriptName, v, nv => o.ScriptName = nv), doc));
        panel.Children.Add(Vec3Row("Position", new List<LevelObject> { lo }, o => o.Position,
            (o, v) => doc.EditValue(o.Section, "Move event", o.Position, v, nv => o.Position = nv, $"pos-{o.Uid}"), doc));
        panel.Children.Add(FloatRow(doc, lo.Section, "Delay (s)", ev.Delay, v => ev.Delay = v));
        panel.Children.Add(ColorRow(doc, lo.Section, "Color", ev.Color, c => ev.Color = c));

        if (schema.HasOrientation)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Directional — orientation persists; press O in the viewport to reorient (arrow drawn).",
                FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
            });
        }

        if (schema.Fields.Count > 0)
        {
            panel.Children.Add(Header("Fields"));
            foreach (EventFieldSpec f in schema.Fields)
            {
                panel.Children.Add(EventFieldRow(doc, lo.Section, ev, f));
            }
        }

        // Links summary + editor.
        panel.Children.Add(Header($"Links ({ev.Links.Count})"));
        panel.Children.Add(new TextBlock
        {
            Text = ev.Links.Count == 0 ? "(none)" : string.Join(", ", ev.Links),
            FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
        });
        var editLinks = new Button { Content = "Edit Links (Ctrl+L)…", Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        editLinks.Click += (_, _) => _host?.Dispatcher.Invoke(CommandIds.ObjEditLinks);
        panel.Children.Add(editLinks);
        return panel;
    }

    private Control EventFieldRow(EditorDocument doc, RflSection section, RflEvent ev, EventFieldSpec f)
    {
        object? current = EventFieldAccess.Get(f, ev);
        switch (f.Editor)
        {
            case EventEditor.Bool:
            case EventEditor.BoolAsInt:
            case EventEditor.FlagChar:
            {
                var check = new CheckBox { IsChecked = current is true };
                check.IsCheckedChanged += (_, _) => CommitField(doc, section, f, ev, check.IsChecked == true);
                return LabeledRow(f.Label, check);
            }

            case EventEditor.Dropdown when f.Options is { Count: > 0 }:
            {
                var combo = new ComboBox { ItemsSource = f.Options, FontSize = 12, MinWidth = 150 };
                if (f.SaveIndex && current is int idx && idx >= 0 && idx < f.Options.Count)
                {
                    combo.SelectedIndex = idx;
                }
                else if (!f.SaveIndex && current is string cs)
                {
                    combo.SelectedItem = f.Options.FirstOrDefault(o => o == cs);
                }

                combo.SelectionChanged += (_, _) =>
                {
                    object? val = f.SaveIndex ? combo.SelectedIndex : combo.SelectedItem as string;
                    if (combo.SelectedIndex >= 0)
                    {
                        CommitField(doc, section, f, ev, val);
                    }
                };
                return LabeledRow(f.Label, combo);
            }

            default:
            {
                var box = new TextBox { Text = current?.ToString() ?? string.Empty, FontSize = 12 };
                box.LostFocus += (_, _) => CommitField(doc, section, f, ev, ParseField(f, box.Text));
                return LabeledRow(f.Label, box);
            }
        }
    }

    private static object? ParseField(EventFieldSpec f, string? text)
    {
        text ??= string.Empty;
        return f.Editor switch
        {
            EventEditor.Int or EventEditor.UidPicker or EventEditor.IntAsFloat =>
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0,
            EventEditor.Float =>
                float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float fl) ? fl : 0f,
            _ => text,
        };
    }

    private void CommitField(EditorDocument doc, RflSection section, EventFieldSpec f, RflEvent ev, object? value)
    {
        object? old = EventFieldAccess.Get(f, ev);
        doc.EditValue(section, "Edit " + f.Label, old, value, v => EventFieldAccess.Set(f, ev, v));
        _host?.RefreshSelectionOverlay();
    }

    // ---- Data-driven object inspector (from the §8 registry) ------------------

    private Control BuildObjectInspector(EditorDocument doc, LevelObject lo)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Header($"{lo.Kind}: {lo.DisplayName}"));
        panel.Children.Add(ReadonlyRow("UID", lo.Uid.ToString(CultureInfo.InvariantCulture)));
        panel.Children.Add(Vec3Row("Position", new List<LevelObject> { lo }, o => o.Position,
            (o, v) => doc.EditValue(o.Section, "Move object", o.Position, v, nv => o.Position = nv, $"pos-{o.Uid}"), doc));
        panel.Children.Add(BoolRow("Hidden", new List<LevelObject> { lo }, o => o.Hidden,
            (o, v) => { doc.EditValue(o.Section, "Toggle hidden", o.Hidden, v, nv => o.Hidden = nv); _host?.RequestSceneRebuild(); }, doc));

        foreach (InspectorField field in ObjectInspectorCatalog.For(lo.Kind))
        {
            panel.Children.Add(BuildRegistryRow(doc, lo, field));
        }

        return panel;
    }

    // ---- Specialized inspectors -------------------------------------------

    /// <summary>
    /// Item 4: the Light inspector gains a "Projection Cookie" row — the schema fields plus a
    /// cookie filename box, a Browse… picker and a Clear button. Writing/clearing the cookie goes
    /// through the object-metadata service (undo-safe) and marks lighting dirty for a re-bake.
    /// </summary>
    private Control BuildLightInspector(EditorDocument doc, LevelObject lo, Light light)
    {
        _ = light;
        var panel = (StackPanel)BuildObjectInspector(doc, lo);
        panel.Children.Add(Header("Projection Cookie"));

        var box = new TextBox
        {
            Text = _host?.GetLightCookie(lo.Uid) ?? string.Empty,
            Watermark = "(no cookie)",
            FontSize = 12,
        };
        box.LostFocus += (_, _) => _host?.SetLightCookie(lo.Uid, box.Text);

        var browse = new Button { Content = "Browse…", Padding = new Avalonia.Thickness(8, 2) };
        browse.Click += async (_, _) =>
        {
            if (_host is { } host && await host.PickCookieImageAsync() is { } picked)
            {
                host.SetLightCookie(lo.Uid, picked);
                Refresh();
            }
        };

        var clear = new Button { Content = "Clear", Padding = new Avalonia.Thickness(8, 2) };
        clear.Click += (_, _) => { _host?.SetLightCookie(lo.Uid, null); Refresh(); };

        panel.Children.Add(box);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
            Children = { browse, clear },
        });

        // Item 6: Sharpness slider (0–100%). Enabled only when a cookie is set. 100% = crisp
        // (raw cookie sample); lower values sample progressively blurred cookie levels.
        bool hasCookie = !string.IsNullOrWhiteSpace(box.Text);
        float sharpness = _host?.GetLightCookieSharpness(lo.Uid) ?? 1f;
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(sharpness * 100f, 0f, 100f),
            IsEnabled = hasCookie,
            TickFrequency = 5,
            IsSnapToTickEnabled = false,
            MinWidth = 140,
        };
        var sharpVal = new TextBlock
        {
            Text = $"{slider.Value:0}%",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 34,
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                sharpVal.Text = $"{slider.Value:0}%";
            }
        };
        // Commit at the end of a drag / on lost-focus (one undo step per drag, not per pixel).
        void CommitSharpness()
        {
            _host?.SetLightCookieSharpness(lo.Uid, (float)(slider.Value / 100.0));
        }

        slider.PointerCaptureLost += (_, _) => CommitSharpness();
        slider.LostFocus += (_, _) => CommitSharpness();
        panel.Children.Add(LabeledRow("Sharpness",
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { slider, sharpVal } }));

        panel.Children.Add(new TextBlock
        {
            Text = "A greyscale image projected as a gobo during lightmap baking (spot: cone gobo; point: spherical). "
                + "Editor-only — never packed. Sharpness controls sampling softness; for a crisp projection also raise "
                + "lightmap resolution (Level ▸ Lightmap Method ▸ High-Resolution Lightmaps).",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private Control BuildAmbientSoundInspector(EditorDocument doc, LevelObject lo, AmbientSound snd)
    {
        var panel = (StackPanel)BuildObjectInspector(doc, lo);
        panel.Children.Add(Header("Preview"));
        bool isWav = snd.SoundFileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        var play = new Button { Content = "▶ Play", IsEnabled = isWav };
        var stop = new Button { Content = "■ Stop" };
        play.Click += (_, _) =>
        {
            if (_host?.PlaySoundPreview(snd.SoundFileName) != true)
            {
                _host?.Dispatcher.ShowMessage("Cannot preview (non-wav, or file not found in the VFS).");
            }
        };
        stop.Click += (_, _) => _host?.StopSoundPreview();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { play, stop } });
        if (!isWav)
        {
            panel.Children.Add(new TextBlock { Text = "Preview supports .wav only (System.Media.SoundPlayer).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        return panel;
    }

    private Control BuildNoteInspector(EditorDocument doc, LevelObject lo, AlpineNoteObject note)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Header($"Note Object: {lo.DisplayName}"));
        panel.Children.Add(ReadonlyRow("UID", lo.Uid.ToString(CultureInfo.InvariantCulture)));
        panel.Children.Add(StringRow("Script Name", new List<LevelObject> { lo }, o => o.ScriptName,
            (o, v) => doc.EditValue(o.Section, "Edit script name", o.ScriptName, v, nv => o.ScriptName = nv), doc));
        panel.Children.Add(Vec3Row("Position", new List<LevelObject> { lo }, o => o.Position,
            (o, v) => doc.EditValue(o.Section, "Move note", o.Position, v, nv => o.Position = nv, $"pos-{o.Uid}"), doc));

        panel.Children.Add(Header($"Notes ({note.Notes.Count})"));
        for (int i = 0; i < note.Notes.Count; i++)
        {
            int idx = i;
            var box = new TextBox { Text = note.Notes[i], FontSize = 12, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            box.LostFocus += (_, _) => ReplaceNotes(doc, lo, note, list => list[idx] = box.Text ?? string.Empty);
            var remove = new Button { Content = "✕", Padding = new Avalonia.Thickness(6, 0) };
            remove.Click += (_, _) => { ReplaceNotes(doc, lo, note, list => list.RemoveAt(idx)); Refresh(); };
            var row = new DockPanel();
            DockPanel.SetDock(remove, Avalonia.Controls.Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(box);
            panel.Children.Add(row);
        }

        var add = new Button { Content = "+ Add Note", Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        add.Click += (_, _) => { ReplaceNotes(doc, lo, note, list => list.Add("New note")); Refresh(); };
        panel.Children.Add(add);
        return panel;
    }

    private void ReplaceNotes(EditorDocument doc, LevelObject lo, AlpineNoteObject note, Action<List<string>> mutate)
    {
        var old = new List<string>(note.Notes);
        var next = new List<string>(note.Notes);
        mutate(next);
        doc.EditValue(lo.Section, "Edit notes", old, next, v => { note.Notes.Clear(); note.Notes.AddRange(v); });
    }

    private Control BuildCoronaInspector(EditorDocument doc, LevelObject lo, AlpineCoronaObject corona)
    {
        var panel = (StackPanel)BuildObjectInspector(doc, lo);
        panel.Children.Add(Header("Color"));
        panel.Children.Add(ColorRowRaw(doc, lo.Section, "RGBA (with swatch)",
            () => new RfColor(corona.ColorR, corona.ColorG, corona.ColorB, corona.ColorA),
            c => { corona.ColorR = c.R; corona.ColorG = c.G; corona.ColorB = c.B; corona.ColorA = c.A; }));
        var swatch = new Border
        {
            Width = 60, Height = 22, BorderBrush = Brushes.Gray, BorderThickness = new Avalonia.Thickness(1),
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(corona.ColorA, corona.ColorR, corona.ColorG, corona.ColorB)),
        };
        panel.Children.Add(LabeledRow("Swatch", swatch));
        return panel;
    }

    // ---- Mesh-object inspector (Alpine mesh.cpp dialog: filename/anim/collision/material,
    //      per-slot texture overrides, and the full clutter-behaviour group) --------------------

    private static readonly string[] MeshMaterialNames =
        { "Default", "Rock", "Metal", "Flesh", "Water", "Lava", "Solid", "Sand", "Ice", "Glass" };

    // Corpse material adds a leading "Automatic" (index 0 → stored -1); indices 1-10 → 0-9.
    private static readonly string[] CorpseMaterialNames =
        { "Automatic", "Default", "Rock", "Metal", "Flesh", "Water", "Lava", "Solid", "Sand", "Ice", "Glass" };

    private static readonly string[] CollisionModeNames = { "None", "Only Weapons", "All" };

    // The ten author-facing damage-type factors (mesh.cpp:779-789); the 11th array slot is
    // unused by Alpine's dialog and simply round-trips untouched.
    private static readonly string[] DamageTypeNames =
        { "Bash", "Bullet", "AP Bullet", "Explosive", "Fire", "Energy", "Electrical", "Acid", "Scalding", "Crush" };

    /// <summary>
    /// The Alpine mesh-object inspector (mesh.cpp:660-825): script/filename (with a Browse picker
    /// and legacy-extension fixup), state anim, collision mode, a named-enum Material, the per-slot
    /// texture-override list editor, and — when Is Clutter is set — the full clutter-behaviour
    /// group (life, debris, explosion, 10 damage-type factors, corpse mesh/anim/collision/material).
    /// Every edit routes through the document's undo system and dirties the mesh-objects section.
    /// </summary>
    private Control BuildMeshInspector(EditorDocument doc, LevelObject lo, AlpineMeshObject mesh)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Header($"Mesh Object: {lo.DisplayName}"));
        panel.Children.Add(ReadonlyRow("UID", lo.Uid.ToString(CultureInfo.InvariantCulture)));
        panel.Children.Add(StringRow("Script Name", new List<LevelObject> { lo }, o => o.ScriptName,
            (o, v) => doc.EditValue(o.Section, "Edit script name", o.ScriptName, v, nv => o.ScriptName = nv), doc));
        panel.Children.Add(Vec3Row("Position", new List<LevelObject> { lo }, o => o.Position,
            (o, v) => doc.EditValue(o.Section, "Move mesh object", o.Position, v, nv => o.Position = nv, $"pos-{o.Uid}"), doc));
        panel.Children.Add(BoolRow("Hidden", new List<LevelObject> { lo }, o => o.Hidden,
            (o, v) => { doc.EditValue(o.Section, "Toggle hidden", o.Hidden, v, nv => o.Hidden = nv); _host?.RequestSceneRebuild(); }, doc));

        // Mesh Filename + Browse (item 10): a .v3m/.v3c/.vfx picker with v3d→v3m / vcm→v3c fixup.
        panel.Children.Add(MeshFilenameRow(doc, lo, mesh));
        panel.Children.Add(MeshStringRow(doc, lo, "State Anim", () => mesh.StateAnim, v => mesh.StateAnim = v, FixAnimExt));
        panel.Children.Add(MeshByteEnumRow(doc, lo, "Collision Mode", CollisionModeNames,
            () => mesh.CollisionMode, v => mesh.CollisionMode = v));
        // Material named enum (item 10) instead of a raw int.
        panel.Children.Add(MeshIntEnumRow(doc, lo, "Material", MeshMaterialNames, () => mesh.Material, v => mesh.Material = v));

        // Per-slot texture overrides (item 7).
        panel.Children.Add(Header($"Texture Overrides ({mesh.TextureOverrides.Count})"));
        BuildTextureOverrideList(panel, doc, lo, mesh);

        // Clutter behaviour (item 6). Toggling on allocates the block (model setter) and reveals the group.
        panel.Children.Add(MeshClutterToggleRow(doc, lo, mesh));
        if (mesh.IsClutter != 0 && mesh.Clutter is { } clutter)
        {
            AppendClutterGroup(panel, doc, lo, clutter);
        }

        return panel;
    }

    private Control MeshFilenameRow(EditorDocument doc, LevelObject lo, AlpineMeshObject mesh)
    {
        var box = new TextBox { Text = mesh.MeshFilename, FontSize = 12 };

        void Commit()
        {
            string next = FixMeshExt(box.Text ?? string.Empty);
            if (next != box.Text)
            {
                box.Text = next; // reflect the legacy-extension fixup back to the field
            }

            if (next != mesh.MeshFilename)
            {
                doc.EditValue(lo.Section, "Edit mesh filename", mesh.MeshFilename, next, v => mesh.MeshFilename = v);
                _host?.RequestSceneRebuild();
            }
        }

        box.LostFocus += (_, _) => Commit();

        var browse = new Button { Content = "Browse…", Padding = new Avalonia.Thickness(8, 2) };
        browse.Click += async (_, _) =>
        {
            if (PickMeshFile is { } pick && await pick() is { Length: > 0 } chosen)
            {
                string next = FixMeshExt(chosen);
                doc.EditValue(lo.Section, "Edit mesh filename", mesh.MeshFilename, next, v => mesh.MeshFilename = v);
                _host?.RequestSceneRebuild();
                Refresh();
            }
        };

        var dock = new DockPanel();
        DockPanel.SetDock(browse, Avalonia.Controls.Dock.Right);
        dock.Children.Add(browse);
        dock.Children.Add(box);
        return LabeledRow("Mesh Filename", dock);
    }

    /// <summary>A text field bound to a mesh-object string, committing one undo step, with an
    /// optional legacy-extension fixup applied on commit (Alpine's EN_CHANGE auto-correct).</summary>
    private Control MeshStringRow(EditorDocument doc, LevelObject lo, string label,
        Func<string> get, Action<string> set, Func<string, string>? fixExt = null)
    {
        var box = new TextBox { Text = get(), FontSize = 12 };
        box.LostFocus += (_, _) =>
        {
            string next = fixExt is null ? box.Text ?? string.Empty : fixExt(box.Text ?? string.Empty);
            if (next != box.Text)
            {
                box.Text = next;
            }

            string old = get();
            if (next != old)
            {
                doc.EditValue(lo.Section, "Edit " + label, old, next, set);
            }
        };
        return LabeledRow(label, box);
    }

    /// <summary>A dropdown bound to a 0-based <see cref="byte"/> enum field (collision mode).</summary>
    private Control MeshByteEnumRow(EditorDocument doc, LevelObject lo, string label, string[] options,
        Func<byte> get, Action<byte> set)
    {
        var combo = new ComboBox { ItemsSource = options, FontSize = 12, MinWidth = 150 };
        int cur = get();
        if (cur >= 0 && cur < options.Length)
        {
            combo.SelectedIndex = cur;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex != get())
            {
                byte old = get();
                doc.EditValue(lo.Section, "Edit " + label, old, (byte)combo.SelectedIndex, set);
            }
        };
        return LabeledRow(label, combo);
    }

    /// <summary>A dropdown bound to a 0-based <see cref="int"/> enum field (material).</summary>
    private Control MeshIntEnumRow(EditorDocument doc, LevelObject lo, string label, string[] options,
        Func<int> get, Action<int> set)
    {
        var combo = new ComboBox { ItemsSource = options, FontSize = 12, MinWidth = 150 };
        int cur = get();
        if (cur >= 0 && cur < options.Length)
        {
            combo.SelectedIndex = cur;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex != get())
            {
                int old = get();
                doc.EditValue(lo.Section, "Edit " + label, old, combo.SelectedIndex, set);
            }
        };
        return LabeledRow(label, combo);
    }

    /// <summary>The Is-Clutter checkbox: toggling on allocates the behaviour block (model setter)
    /// and rebuilds the panel so the clutter group appears/disappears (one undo step).</summary>
    private Control MeshClutterToggleRow(EditorDocument doc, LevelObject lo, AlpineMeshObject mesh)
    {
        var check = new CheckBox { IsChecked = mesh.IsClutter != 0 };
        check.IsCheckedChanged += (_, _) =>
        {
            byte old = mesh.IsClutter;
            byte next = check.IsChecked == true ? (byte)1 : (byte)0;
            if (next != old)
            {
                doc.EditValue(lo.Section, "Toggle Is Clutter", old, next, v => mesh.IsClutter = v);
                Refresh();
            }
        };
        return LabeledRow("Is Clutter", check);
    }

    private void AppendClutterGroup(StackPanel panel, EditorDocument doc, LevelObject lo, AlpineMeshClutterInfo c)
    {
        panel.Children.Add(Header("Clutter Behaviour"));
        panel.Children.Add(FloatRow(doc, lo.Section, "Life", c.Life, v => c.Life = v));
        panel.Children.Add(MeshStringRow(doc, lo, "Debris Filename", () => c.DebrisFilename, v => c.DebrisFilename = v, FixDebrisExt));
        panel.Children.Add(MeshStringRow(doc, lo, "Explosion Vclip", () => c.ExplosionVclip, v => c.ExplosionVclip = v));
        panel.Children.Add(FloatRow(doc, lo.Section, "Explosion Radius", c.ExplosionRadius, v => c.ExplosionRadius = v));
        panel.Children.Add(FloatRow(doc, lo.Section, "Debris Velocity", c.DebrisVelocity, v => c.DebrisVelocity = v));

        panel.Children.Add(Header("Corpse"));
        panel.Children.Add(MeshStringRow(doc, lo, "Corpse Filename", () => c.CorpseFilename, v => c.CorpseFilename = v, FixMeshExt));
        panel.Children.Add(MeshStringRow(doc, lo, "Corpse State Anim", () => c.CorpseStateAnim, v => c.CorpseStateAnim = v, FixAnimExt));
        panel.Children.Add(MeshByteEnumRow(doc, lo, "Corpse Collision", CollisionModeNames,
            () => c.CorpseCollision, v => c.CorpseCollision = v));
        panel.Children.Add(CorpseMaterialRow(doc, lo, c));

        panel.Children.Add(Header("Damage Type Factors"));
        for (int i = 0; i < DamageTypeNames.Length && i < c.DamageTypeFactors.Length; i++)
        {
            int idx = i;
            panel.Children.Add(FloatRow(doc, lo.Section, DamageTypeNames[i],
                c.DamageTypeFactors[idx], v => c.DamageTypeFactors[idx] = v));
        }
    }

    /// <summary>Corpse-material dropdown: index 0 = Automatic (stored -1), 1-10 = materials 0-9.</summary>
    private Control CorpseMaterialRow(EditorDocument doc, LevelObject lo, AlpineMeshClutterInfo c)
    {
        var combo = new ComboBox { ItemsSource = CorpseMaterialNames, FontSize = 12, MinWidth = 150 };
        combo.SelectedIndex = c.CorpseMaterial >= 0 && c.CorpseMaterial <= 9 ? c.CorpseMaterial + 1 : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0)
            {
                return;
            }

            sbyte next = combo.SelectedIndex == 0 ? (sbyte)-1 : (sbyte)(combo.SelectedIndex - 1);
            if (next != c.CorpseMaterial)
            {
                sbyte old = c.CorpseMaterial;
                doc.EditValue(lo.Section, "Edit Corpse Material", old, next, v => c.CorpseMaterial = v);
            }
        };
        return LabeledRow("Corpse Material", combo);
    }

    // ---- Texture-override list editor (item 7) --------------------------------

    private void BuildTextureOverrideList(StackPanel panel, EditorDocument doc, LevelObject lo, AlpineMeshObject mesh)
    {
        for (int i = 0; i < mesh.TextureOverrides.Count; i++)
        {
            int idx = i;
            AlpineMeshTextureOverride o = mesh.TextureOverrides[i];

            var slot = new TextBox { Text = o.SlotId.ToString(CultureInfo.InvariantCulture), Width = 48, FontSize = 12 };
            slot.LostFocus += (_, _) =>
            {
                if (byte.TryParse(slot.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte s))
                {
                    ReplaceOverrides(doc, lo, mesh, list => list[idx].SlotId = s);
                }
            };

            var file = new TextBox { Text = o.Filename, FontSize = 12 };
            file.LostFocus += (_, _) => ReplaceOverrides(doc, lo, mesh, list => list[idx].Filename = file.Text ?? string.Empty);

            var remove = new Button { Content = "✕", Padding = new Avalonia.Thickness(6, 0) };
            remove.Click += (_, _) => { ReplaceOverrides(doc, lo, mesh, list => list.RemoveAt(idx)); Refresh(); };

            var row = new DockPanel { Margin = new Avalonia.Thickness(0, 1) };
            DockPanel.SetDock(remove, Avalonia.Controls.Dock.Right);
            DockPanel.SetDock(slot, Avalonia.Controls.Dock.Left);
            row.Children.Add(remove);
            row.Children.Add(slot);
            row.Children.Add(file);
            panel.Children.Add(row);
        }

        var add = new Button { Content = "+ Add Override", Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        add.Click += (_, _) =>
        {
            ReplaceOverrides(doc, lo, mesh, list =>
            {
                byte nextSlot = list.Count == 0 ? (byte)0 : (byte)(list.Max(x => x.SlotId) + 1);
                list.Add(new AlpineMeshTextureOverride { SlotId = nextSlot, Filename = "texture.tga" });
            });
            Refresh();
        };
        panel.Children.Add(add);
    }

    private void ReplaceOverrides(EditorDocument doc, LevelObject lo, AlpineMeshObject mesh,
        Action<List<AlpineMeshTextureOverride>> mutate)
    {
        List<AlpineMeshTextureOverride> old = mesh.TextureOverrides.Select(CloneOverride).ToList();
        List<AlpineMeshTextureOverride> next = mesh.TextureOverrides.Select(CloneOverride).ToList();
        mutate(next);
        doc.EditValue(lo.Section, "Edit texture overrides", old, next,
            v => { mesh.TextureOverrides.Clear(); mesh.TextureOverrides.AddRange(v.Select(CloneOverride)); });
    }

    private static AlpineMeshTextureOverride CloneOverride(AlpineMeshTextureOverride o) =>
        new() { SlotId = o.SlotId, Filename = o.Filename };

    // Legacy-extension fixups mirroring Alpine's mesh-dialog EN_CHANGE handlers (mesh.cpp:833-884).
    private static string FixMeshExt(string s) => ReplaceExt(ReplaceExt(s, ".v3d", ".v3m"), ".vcm", ".v3c");

    private static string FixDebrisExt(string s) => ReplaceExt(s, ".v3d", ".v3m");

    private static string FixAnimExt(string s) => ReplaceExt(s, ".mvf", ".rfa");

    private static string ReplaceExt(string s, string from, string to) =>
        s.EndsWith(from, StringComparison.OrdinalIgnoreCase) ? s[..^from.Length] + to : s;

    // ---- Room-effect inspector (RED "Room Effect" dialog: type + room flags + liquid/ambient) ----

    /// <summary>
    /// The room-effect inspector: the shared object header (uid/script/pos/hidden), the Effect
    /// Type selector (Sky Room / Liquid Room / Ambient Light / None), the three room flags
    /// (Cold / Outside / Air Lock), and — depending on the type — an ambient-light colour or the
    /// full liquid-room properties block (waveform, depth, texture, colour, visibility, liquid
    /// type, plankton, pixels-per-metre, angle, scroll rate). Changing the effect type provisions
    /// the required nested block so the section still serializes. Every edit is one undo step and
    /// dirties the room_effects section.
    /// </summary>
    private Control BuildRoomEffectInspector(EditorDocument doc, LevelObject lo, RoomEffect fx)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Header($"Room Effect (uid {lo.Uid})"));
        panel.Children.Add(ReadonlyRow("UID", lo.Uid.ToString(CultureInfo.InvariantCulture)));
        panel.Children.Add(StringRow("Script Name", new List<LevelObject> { lo }, o => o.ScriptName,
            (o, v) => doc.EditValue(o.Section, "Edit script name", o.ScriptName, v, nv => o.ScriptName = nv), doc));
        panel.Children.Add(Vec3Row("Position", new List<LevelObject> { lo }, o => o.Position,
            (o, v) => doc.EditValue(o.Section, "Move room effect", o.Position, v, nv => o.Position = nv, $"pos-{o.Uid}"), doc));
        panel.Children.Add(BoolRow("Hidden", new List<LevelObject> { lo }, o => o.Hidden,
            (o, v) => { doc.EditValue(o.Section, "Toggle hidden", o.Hidden, v, nv => o.Hidden = nv); _host?.RequestSceneRebuild(); }, doc));

        // Effect Type (1..4). Changing it provisions the ambient/liquid block and rebuilds the panel.
        string[] typeOptions = { "Sky Room", "Liquid Room", "Ambient Light", "None" };
        var typeCombo = new ComboBox { ItemsSource = typeOptions, FontSize = 12, MinWidth = 150 };
        if (fx.EffectType >= 1 && fx.EffectType <= typeOptions.Length)
        {
            typeCombo.SelectedIndex = fx.EffectType - 1;
        }

        typeCombo.SelectionChanged += (_, _) =>
        {
            int newType = typeCombo.SelectedIndex + 1;
            if (typeCombo.SelectedIndex >= 0 && newType != fx.EffectType)
            {
                ChangeEffectType(doc, lo, fx, newType);
            }
        };
        panel.Children.Add(LabeledRow("Effect Type", typeCombo));

        panel.Children.Add(ByteBoolRow(doc, lo.Section, "Room Is Cold", () => fx.RoomIsCold, v => fx.RoomIsCold = v));
        panel.Children.Add(ByteBoolRow(doc, lo.Section, "Room Is Outside", () => fx.RoomIsOutside, v => fx.RoomIsOutside = v));
        panel.Children.Add(ByteBoolRow(doc, lo.Section, "Room Is Air Lock", () => fx.RoomIsAirLock, v => fx.RoomIsAirLock = v));

        if (fx.EffectType == RoomEffectsSection.EffectAmbientLight)
        {
            panel.Children.Add(Header("Ambient Light"));
            panel.Children.Add(ColorRowRaw(doc, lo.Section, "Color",
                () => fx.AmbientLightColor ?? default, c => fx.AmbientLightColor = c));
        }
        else if (fx.EffectType == RoomEffectsSection.EffectLiquidRoom && fx.LiquidProperties is { } lp)
        {
            panel.Children.Add(Header("Liquid Properties"));
            panel.Children.Add(EnumIntRow(doc, lo.Section, "Waveform", new[] { "None", "Calm", "Choppy" },
                () => lp.Waveform, v => lp.Waveform = v));
            panel.Children.Add(FloatRow(doc, lo.Section, "Depth", lp.Depth, v => lp.Depth = v));
            panel.Children.Add(SectionStringRow(doc, lo.Section, "Surface Texture",
                () => lp.SurfaceTexture, v => lp.SurfaceTexture = v));
            panel.Children.Add(ColorRowRaw(doc, lo.Section, "Liquid Color", () => lp.LiquidColor, c => lp.LiquidColor = c));
            panel.Children.Add(FloatRow(doc, lo.Section, "Visibility", lp.Visibility, v => lp.Visibility = v));
            panel.Children.Add(EnumIntRow(doc, lo.Section, "Liquid Type", new[] { "Water", "Lava", "Acid" },
                () => lp.LiquidType, v => lp.LiquidType = v));
            panel.Children.Add(ByteBoolRow(doc, lo.Section, "Contains Plankton",
                () => lp.ContainsPlankton, v => lp.ContainsPlankton = v));
            panel.Children.Add(IntRow(doc, lo.Section, "Pixels/Meter U",
                lp.TexturePixelsPerMeterU, v => lp.TexturePixelsPerMeterU = v));
            panel.Children.Add(IntRow(doc, lo.Section, "Pixels/Meter V",
                lp.TexturePixelsPerMeterV, v => lp.TexturePixelsPerMeterV = v));
            panel.Children.Add(FloatRow(doc, lo.Section, "Texture Angle (deg)",
                lp.TextureAngleDegrees, v => lp.TextureAngleDegrees = v));
            panel.Children.Add(FloatRow(doc, lo.Section, "Scroll Rate U",
                lp.TextureScrollRate.U, v => lp.TextureScrollRate = new Uv(v, lp.TextureScrollRate.V)));
            panel.Children.Add(FloatRow(doc, lo.Section, "Scroll Rate V",
                lp.TextureScrollRate.V, v => lp.TextureScrollRate = new Uv(lp.TextureScrollRate.U, v)));
        }

        return panel;
    }

    /// <summary>
    /// Dedicated inspector for an EAX environmental-audio effect zone (B3): the shared header rows
    /// (UID, script name, position, hidden) plus its reverb-preset name (<see cref="EaxEffect.EffectType"/>,
    /// a free-form vstring). Mirrors <see cref="BuildRoomEffectInspector"/>; every edit is one undo
    /// step and dirties the eax_effects section, and a no-op leaves the section byte-identical.
    /// </summary>
    private Control BuildEaxInspector(EditorDocument doc, LevelObject lo, EaxEffect eax)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Header($"EAX Effect (uid {lo.Uid})"));
        panel.Children.Add(ReadonlyRow("UID", lo.Uid.ToString(CultureInfo.InvariantCulture)));
        panel.Children.Add(StringRow("Script Name", new List<LevelObject> { lo }, o => o.ScriptName,
            (o, v) => doc.EditValue(o.Section, "Edit script name", o.ScriptName, v, nv => o.ScriptName = nv), doc));
        panel.Children.Add(Vec3Row("Position", new List<LevelObject> { lo }, o => o.Position,
            (o, v) => doc.EditValue(o.Section, "Move EAX effect", o.Position, v, nv => o.Position = nv, $"pos-{o.Uid}"), doc));
        panel.Children.Add(BoolRow("Hidden", new List<LevelObject> { lo }, o => o.Hidden,
            (o, v) => { doc.EditValue(o.Section, "Toggle hidden", o.Hidden, v, nv => o.Hidden = nv); _host?.RequestSceneRebuild(); }, doc));
        panel.Children.Add(SectionStringRow(doc, lo.Section, "Effect Type",
            () => eax.EffectType, v => eax.EffectType = v));
        return panel;
    }

    /// <summary>
    /// Changes a room effect's type as one undo step, provisioning the nested block the new type
    /// requires (an ambient colour for Ambient Light, a default liquid block for Liquid Room) so
    /// the section still serializes. Rebuilds the inspector to reflect the new type's fields.
    /// </summary>
    private void ChangeEffectType(EditorDocument doc, LevelObject lo, RoomEffect fx, int newType)
    {
        using (doc.Undo.BeginTransaction("Change room-effect type"))
        {
            doc.EditValue(lo.Section, "Effect type", fx.EffectType, newType, v => fx.EffectType = v);

            if (newType == RoomEffectsSection.EffectAmbientLight && fx.AmbientLightColor is null)
            {
                doc.EditValue(lo.Section, "Init ambient color", fx.AmbientLightColor,
                    (RfColor?)new RfColor(128, 128, 128, 255), v => fx.AmbientLightColor = v);
            }
            else if (newType == RoomEffectsSection.EffectLiquidRoom && fx.LiquidProperties is null)
            {
                doc.EditValue(lo.Section, "Init liquid props", fx.LiquidProperties,
                    (RoomEffectLiquidProperties?)new RoomEffectLiquidProperties { Waveform = 1, LiquidType = 1 },
                    v => fx.LiquidProperties = v);
            }
        }

        _host?.RequestSceneRebuild();
        Refresh();
    }

    /// <summary>A checkbox bound to a 0/1 <see cref="byte"/> field, committing one undo step.</summary>
    private Control ByteBoolRow(EditorDocument doc, RflSection section, string label, Func<byte> get, Action<byte> set)
    {
        var check = new CheckBox { IsChecked = get() != 0 };
        check.IsCheckedChanged += (_, _) =>
        {
            byte old = get();
            byte val = check.IsChecked == true ? (byte)1 : (byte)0;
            if (val != old)
            {
                doc.EditValue(section, "Edit " + label, old, val, set);
                _host?.RefreshSelectionOverlay();
            }
        };
        return LabeledRow(label, check);
    }

    /// <summary>An integer text field bound to a section, committing one undo step on commit.</summary>
    private Control IntRow(EditorDocument doc, RflSection section, string label, int value, Action<int> set)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), FontSize = 12 };
        box.LostFocus += (_, _) =>
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            {
                doc.EditValue(section, "Edit " + label, value, v, set);
            }
        };
        return LabeledRow(label, box);
    }

    /// <summary>A dropdown bound to a 1-based enum int field (option i => value i+1).</summary>
    private Control EnumIntRow(EditorDocument doc, RflSection section, string label, string[] options,
        Func<int> get, Action<int> set)
    {
        var combo = new ComboBox { ItemsSource = options, FontSize = 12, MinWidth = 150 };
        int cur = get();
        if (cur >= 1 && cur <= options.Length)
        {
            combo.SelectedIndex = cur - 1;
        }

        combo.SelectionChanged += (_, _) =>
        {
            int val = combo.SelectedIndex + 1;
            if (combo.SelectedIndex >= 0 && val != get())
            {
                doc.EditValue(section, "Edit " + label, get(), val, set);
                _host?.RefreshSelectionOverlay();
            }
        };
        return LabeledRow(label, combo);
    }

    /// <summary>A text field bound to a section-owned string, committing one undo step.</summary>
    private Control SectionStringRow(EditorDocument doc, RflSection section, string label, Func<string> get, Action<string> set)
    {
        var box = new TextBox { Text = get(), FontSize = 12 };
        box.LostFocus += (_, _) =>
        {
            string old = get();
            string next = box.Text ?? string.Empty;
            if (next != old)
            {
                doc.EditValue(section, "Edit " + label, old, next, set);
                _host?.RefreshSelectionOverlay();
            }
        };
        return LabeledRow(label, box);
    }

    private Control BuildRegistryRow(EditorDocument doc, LevelObject lo, InspectorField field)
    {
        object model = lo.Model;

        // Virtual fields need model-specific handling; trigger MP flags are editable.
        if (field.Virtual)
        {
            return BuildVirtualRow(doc, lo, field);
        }

        object? current = field.Get(model);
        switch (field.Editor)
        {
            case InspectorEditor.Bool:
            {
                var check = new CheckBox { IsChecked = current is true };
                if (field.Note is not null)
                {
                    ToolTip.SetTip(check, field.Note);
                }

                check.IsCheckedChanged += (_, _) => CommitRegistry(doc, lo, field, check.IsChecked == true);
                return LabeledRow(field.Label + (field.EditorOnly ? " (editor)" : string.Empty), check);
            }

            case InspectorEditor.Enum when field.Options is { Count: > 0 }:
            {
                var combo = new ComboBox { ItemsSource = field.Options, FontSize = 12, MinWidth = 150 };
                if (current is int i && i >= 0 && i < field.Options.Count)
                {
                    combo.SelectedIndex = i;
                }

                combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { CommitRegistry(doc, lo, field, combo.SelectedIndex); } };
                return LabeledRow(field.Label, combo);
            }

            case InspectorEditor.Color:
                return ColorRowRaw(doc, lo.Section, field.Label, () => current is RfColor c ? c : default, nc => field.Set(model, nc));

            case InspectorEditor.Vector:
                return Vec3Editor(field.Label, current is Vec3 v ? v : default, false, nv => Commit(doc, () => doc.EditValue(lo.Section, "Edit " + field.Label, field.Get(model), nv, x => field.Set(model, x))));

            default:
            {
                var box = new TextBox { Text = current?.ToString() ?? string.Empty, FontSize = 12 };
                box.LostFocus += (_, _) => CommitRegistry(doc, lo, field, ParseRegistry(field, box.Text));
                return LabeledRow(field.Label, box);
            }
        }
    }

    private Control BuildVirtualRow(EditorDocument doc, LevelObject lo, InspectorField field)
    {
        // Trigger MP flags (0xAB script-name encoding) are editable checkboxes.
        if (lo.Model is Trigger t && field.Label.StartsWith("MP ", StringComparison.Ordinal))
        {
            int bit = field.Label switch
            {
                "MP Clientside" => 0x2,
                "MP Solo" => 0x4,
                "MP Solo Ignore Resets" => 0x8,
                _ => 0,
            };
            bool on = t.IsPureFactionEncoded && (t.ScriptName[1] & bit) != 0;
            var check = new CheckBox { IsChecked = on };
            if (field.Note is not null)
            {
                ToolTip.SetTip(check, field.Note);
            }

            check.IsCheckedChanged += (_, _) =>
            {
                Commit(doc, () =>
                {
                    int flags = t.IsPureFactionEncoded ? t.ScriptName[1] : 0;
                    flags = check.IsChecked == true ? flags | bit : flags & ~bit;

                    // Preserve the trigger's real script name that follows the flag byte; toggling a
                    // flag must never discard it (Alpine PutFlagsIntoScriptName, trigger.cpp:64-72).
                    string baseName = t.IsPureFactionEncoded ? t.ScriptName[2..] : t.ScriptName;
                    string old = t.ScriptName;
                    string next = flags == 0 ? baseName : "«" + (char)flags + baseName;
                    doc.EditValue(lo.Section, "Edit " + field.Label, old, next, v => t.ScriptName = v);
                });
            };
            return LabeledRow(field.Label, check);
        }

        // Other virtual fields (editor-only light section membership) are display-only.
        return LabeledRow(field.Label, new TextBlock
        {
            Text = "(display-only)", FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private static object? ParseRegistry(InspectorField field, string? text)
    {
        text ??= string.Empty;
        return field.Editor switch
        {
            InspectorEditor.Int or InspectorEditor.Uid =>
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0,
            InspectorEditor.Float =>
                float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f,
            _ => text,
        };
    }

    private void CommitRegistry(EditorDocument doc, LevelObject lo, InspectorField field, object? value)
    {
        object model = lo.Model;
        object? old = field.Get(model);
        doc.EditValue(lo.Section, "Edit " + field.Label, old, value, v => field.Set(model, v));
        _host?.RefreshSelectionOverlay();
    }

    // ---- Brush inspector (BrushInspectorCatalog over the brush selection) ------

    /// <summary>
    /// The per-face inspector (item 0f): a header + the shared <see cref="FacePropsControl"/>
    /// (the exact editor Face mode's Texture/UV tab uses), so face properties are editable from
    /// the Properties panel too. The "Pick…" button opens the full texture browser.
    /// </summary>
    private Control BuildFaceInspector(BrushEditor be)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Header($"{be.SelectedFaces.Count} face(s)"));
        var editor = new FacePropsControl();
        editor.Bind(
            be,
            afterEdit: () => { _host?.RefreshSelectionOverlay(); },
            report: msg => _host?.Dispatcher.ShowMessage(msg),
            openTexturePicker: () => _host?.FocusTextureTools(),
            armEyedropper: onSampled => _host?.ArmTextureEyedropper(onSampled));
        panel.Children.Add(editor);
        return panel;
    }

    private Control BuildBrushInspector(EditorDocument doc, BrushEditor be, List<Brush> brushes)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Header(brushes.Count == 1
            ? $"Brush {brushes[0].Uid}"
            : $"{brushes.Count} brushes"));

        foreach (InspectorField field in BrushInspectorCatalog.Fields)
        {
            panel.Children.Add(BuildBrushRow(doc, be, brushes, field));
        }

        return panel;
    }

    private Control BuildBrushRow(EditorDocument doc, BrushEditor be, List<Brush> brushes, InspectorField field)
    {
        if (field.Virtual)
        {
            return BuildBrushVirtualRow(doc, be, brushes, field);
        }

        // Flags / Life resolve on the Brush model via the shared metadata. Edits
        // route through BrushEditor.EditBrushes: one undo entry for the whole
        // selection, brushes section dirty, BrushesChanged -> geometry-dirty + scene.
        switch (field.Editor)
        {
            case InspectorEditor.Bool:
            {
                bool mixed = brushes.Select(b => field.Get(b)).Distinct().Count() > 1;
                var check = new CheckBox { IsChecked = mixed ? null : field.Get(brushes[0]) is true, IsThreeState = mixed };
                if (field.Note is not null)
                {
                    ToolTip.SetTip(check, field.Note);
                }

                check.IsCheckedChanged += (_, _) =>
                {
                    bool val = check.IsChecked ?? false;
                    check.IsThreeState = false;
                    CommitBrushes(be, brushes, field.Label, b =>
                    {
                        field.Set(b, val);
                        b.Flags = BrushInspectorCatalog.Normalize(b.Flags);
                    });
                };
                return LabeledRow(field.Label, check);
            }

            case InspectorEditor.Enum when field.Options is { Count: > 0 }:
            {
                bool mixed = brushes.Select(b => field.Get(b)).Distinct().Count() > 1;
                var combo = new ComboBox { ItemsSource = field.Options, FontSize = 12, MinWidth = 120 };
                if (!mixed && field.Get(brushes[0]) is int i && i >= 0 && i < field.Options.Count)
                {
                    combo.SelectedIndex = i;
                }

                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex >= 0)
                    {
                        int val = combo.SelectedIndex;
                        CommitBrushes(be, brushes, field.Label, b =>
                        {
                            field.Set(b, val);
                            b.Flags = BrushInspectorCatalog.Normalize(b.Flags);
                        });
                    }
                };
                return LabeledRow(field.Label, combo);
            }

            default:
            {
                object?[] values = brushes.Select(b => field.Get(b)).ToArray();
                bool mixed = values.Select(v => v?.ToString()).Distinct().Count() > 1;
                var box = new TextBox
                {
                    Text = mixed ? string.Empty : Convert.ToString(values[0], CultureInfo.InvariantCulture),
                    Watermark = mixed ? Mixed : null,
                    FontSize = 12,
                };
                box.LostFocus += (_, _) =>
                {
                    if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    {
                        CommitBrushes(be, brushes, field.Label, b => field.Set(b, v));
                    }
                };
                return LabeledRow(field.Label, box);
            }
        }
    }

    private Control BuildBrushVirtualRow(EditorDocument doc, BrushEditor be, List<Brush> brushes, InspectorField field)
    {
        switch (field.Label)
        {
            case "UID":
                return ReadonlyRow(field.Label, brushes.Count == 1
                    ? brushes[0].Uid.ToString(CultureInfo.InvariantCulture)
                    : Mixed);

            case "Time Index":
                return ReadonlyRow(field.Label, brushes.Count == 1
                    ? be.TimeIndex(brushes[0].Uid).ToString(CultureInfo.InvariantCulture)
                    : Mixed);

            case "Locked":
            {
                bool mixed = brushes.Select(b => b.State == BrushState.Locked).Distinct().Count() > 1;
                var check = new CheckBox { IsChecked = mixed ? null : brushes[0].State == BrushState.Locked, IsThreeState = mixed };
                if (field.Note is not null)
                {
                    ToolTip.SetTip(check, field.Note);
                }

                check.IsCheckedChanged += (_, _) =>
                {
                    bool val = check.IsChecked ?? false;
                    check.IsThreeState = false;
                    CommitBrushes(be, brushes, field.Label, b => b.State = val ? BrushState.Locked : BrushState.Normal);
                };
                return LabeledRow(field.Label, check);
            }

            case "Material":
            {
                bool mixed = brushes.Select(b => BrushBreakableProps.GetMaterial(doc, b.Uid)).Distinct().Count() > 1;
                var combo = new ComboBox { ItemsSource = field.Options, FontSize = 12, MinWidth = 120 };
                if (!mixed)
                {
                    int cur = BrushBreakableProps.GetMaterial(doc, brushes[0].Uid);
                    combo.SelectedIndex = cur >= 0 && cur < (field.Options?.Count ?? 0) ? cur : 0;
                }

                if (field.Note is not null)
                {
                    ToolTip.SetTip(combo, field.Note);
                }

                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex >= 0)
                    {
                        Commit(doc, () =>
                        {
                            foreach (Brush b in brushes)
                            {
                                BrushBreakableProps.SetMaterial(doc, b.Uid, combo.SelectedIndex);
                            }
                        });
                    }
                };
                return LabeledRow(field.Label, combo);
            }

            case "No Debris":
            {
                bool mixed = brushes.Select(b => BrushBreakableProps.GetNoDebris(doc, b.Uid)).Distinct().Count() > 1;
                var check = new CheckBox { IsChecked = mixed ? null : BrushBreakableProps.GetNoDebris(doc, brushes[0].Uid), IsThreeState = mixed };
                if (field.Note is not null)
                {
                    ToolTip.SetTip(check, field.Note);
                }

                check.IsCheckedChanged += (_, _) =>
                {
                    bool val = check.IsChecked ?? false;
                    check.IsThreeState = false;
                    Commit(doc, () =>
                    {
                        foreach (Brush b in brushes)
                        {
                            BrushBreakableProps.SetNoDebris(doc, b.Uid, val);
                        }
                    });
                };
                return LabeledRow(field.Label, check);
            }

            default:
                return ReadonlyRow(field.Label, Mixed);
        }
    }

    private void CommitBrushes(BrushEditor be, List<Brush> brushes, string label, Action<Brush> mutate)
    {
        var uids = brushes.Select(b => b.Uid).ToArray();
        OpResult r = be.EditBrushes(uids, "Edit " + label, b => { mutate(b); return OpResult.Ok(); });
        if (!r.Success)
        {
            _host?.Dispatcher.ShowMessage(r.Message);
            return;
        }

        _host?.RefreshSelectionOverlay();
        _host?.Dispatcher.ShowMessage("Edited " + label);
    }

    private Control FloatRow(EditorDocument doc, RflSection section, string label, float value, Action<float> set)
    {
        var box = new TextBox { Text = F(value), FontSize = 12 };
        box.LostFocus += (_, _) =>
        {
            if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                doc.EditValue(section, "Edit " + label, value, v, set);
            }
        };
        return LabeledRow(label, box);
    }

    private Control ColorRow(EditorDocument doc, RflSection section, string label, RfColor color, Action<RfColor> set) =>
        ColorRowRaw(doc, section, label, () => color, set);

    private Control ColorRowRaw(EditorDocument doc, RflSection section, string label, Func<RfColor> get, Action<RfColor> set)
    {
        RfColor c = get();
        TextBox r = NumBox(c.R.ToString(CultureInfo.InvariantCulture));
        TextBox g = NumBox(c.G.ToString(CultureInfo.InvariantCulture));
        TextBox b = NumBox(c.B.ToString(CultureInfo.InvariantCulture));
        TextBox a = NumBox(c.A.ToString(CultureInfo.InvariantCulture));

        void CommitColor()
        {
            if (byte.TryParse(r.Text, out byte rr) && byte.TryParse(g.Text, out byte gg) &&
                byte.TryParse(b.Text, out byte bb) && byte.TryParse(a.Text, out byte aa))
            {
                RfColor old = get();
                var nc = new RfColor(rr, gg, bb, aa);
                doc.EditValue(section, "Edit " + label, old, nc, set);
            }
        }

        foreach (TextBox t in new[] { r, g, b, a })
        {
            t.Width = 42;
            t.LostFocus += (_, _) => CommitColor();
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        row.Children.Add(r);
        row.Children.Add(g);
        row.Children.Add(b);
        row.Children.Add(a);
        return LabeledRow(label, row);
    }

    /// <summary>Shows a raw model (e.g. level properties) rather than a selection.</summary>
    public void ShowRaw(EditorDocument doc, object model, RflSection section, string title)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Header(title));
        AppendReflectedRows(panel, doc, new[] { (model, section) }, exclude: null);
        SetContent(panel);
    }

    private Control BuildGrid(EditorDocument doc, List<LevelObject> sel)
    {
        var panel = new StackPanel { Spacing = 2 };
        LevelObject first = sel[0];
        bool sameKind = sel.All(o => o.Kind == first.Kind);
        panel.Children.Add(Header(sel.Count == 1
            ? $"{first.Kind}: {first.DisplayName}"
            : $"{sel.Count} objects" + (sameKind ? $" ({first.Kind})" : " (mixed)")));

        // Common fields (uniform across all object kinds).
        panel.Children.Add(ReadonlyRow("UID", sel.Count == 1 ? first.Uid.ToString(CultureInfo.InvariantCulture) : Mixed));
        panel.Children.Add(StringRow("Script Name", sel, o => o.ScriptName,
            (o, v) => doc.EditValue(o.Section, "Edit script name", o.ScriptName, v, nv => o.ScriptName = nv), doc));
        panel.Children.Add(Vec3Row("Position", sel, o => o.Position,
            (o, v) => doc.EditValue(o.Section, "Move object", o.Position, v, nv => o.Position = nv, $"pos-{o.Uid}"), doc));
        panel.Children.Add(BoolRow("Hidden", sel, o => o.Hidden,
            (o, v) =>
            {
                doc.EditValue(o.Section, "Toggle hidden", o.Hidden, v, nv => o.Hidden = nv);
                _host?.RequestSceneRebuild();
            }, doc));

        // Type-specific simple fields (only when the whole selection shares a kind).
        if (sameKind)
        {
            var targets = sel.Select(o => (o.Model, o.Section)).ToList();
            AppendReflectedRows(panel, doc, targets, exclude: CommonPropertyNames);
        }

        return panel;
    }

    private static readonly HashSet<string> CommonPropertyNames = new(StringComparer.Ordinal)
    {
        "Uid", "ScriptName", "Position", "HiddenInEditor", "Header", "Rotation", "Orientation", "Links",
    };

    private void AppendReflectedRows(
        StackPanel panel, EditorDocument doc, IReadOnlyList<(object Model, RflSection Section)> targets, HashSet<string>? exclude)
    {
        (object Model, RflSection Section) first = targets[0];
        foreach (PropertyInfo prop in first.Model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (exclude is not null && exclude.Contains(prop.Name))
            {
                continue;
            }

            Type t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (!IsEditable(t))
            {
                continue;
            }

            Control? row = BuildReflectedRow(doc, targets, prop, t);
            if (row is not null)
            {
                panel.Children.Add(row);
            }
        }
    }

    private static bool IsEditable(Type t) =>
        t == typeof(string) || t == typeof(bool) || t.IsEnum || t == typeof(Vec3) ||
        t == typeof(float) || t == typeof(double) ||
        t == typeof(int) || t == typeof(uint) || t == typeof(short) || t == typeof(ushort) ||
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(long);

    private Control? BuildReflectedRow(
        EditorDocument doc, IReadOnlyList<(object Model, RflSection Section)> targets, PropertyInfo prop, Type t)
    {
        string label = Humanize(prop.Name);

        if (t == typeof(bool))
        {
            return BoolRowRaw(label, targets, m => (bool)(prop.GetValue(m) ?? false),
                (m, v) => prop.SetValue(m, v), doc, prop.Name);
        }

        if (t.IsEnum)
        {
            return EnumRow(label, targets, prop, t, doc);
        }

        if (t == typeof(Vec3))
        {
            return Vec3RowRaw(label, targets, m => (Vec3)(prop.GetValue(m) ?? default(Vec3)),
                (m, v) => prop.SetValue(m, v), doc, prop.Name);
        }

        if (t == typeof(string))
        {
            return StringRowRaw(label, targets, m => (string)(prop.GetValue(m) ?? string.Empty),
                (m, v) => prop.SetValue(m, v), doc, prop.Name);
        }

        // Numeric.
        return NumberRow(label, targets, prop, t, doc);
    }

    // ---- Row builders over LevelObject selection ----

    private Control StringRow(string label, List<LevelObject> sel, Func<LevelObject, string> get,
        Action<LevelObject, string> set, EditorDocument doc)
    {
        bool mixed = sel.Select(get).Distinct().Count() > 1;
        var box = new TextBox { Text = mixed ? string.Empty : get(sel[0]), Watermark = mixed ? Mixed : null, FontSize = 12 };
        box.LostFocus += (_, _) => Commit(doc, () => { foreach (LevelObject o in sel) { set(o, box.Text ?? string.Empty); } });
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(LostFocusEvent)); } };
        return LabeledRow(label, box);
    }

    private Control Vec3Row(string label, List<LevelObject> sel, Func<LevelObject, Vec3> get,
        Action<LevelObject, Vec3> set, EditorDocument doc)
    {
        Vec3 v = get(sel[0]);
        bool mixed = sel.Select(get).Distinct().Count() > 1;
        return Vec3Editor(label, v, mixed, nv => Commit(doc, () => { foreach (LevelObject o in sel) { set(o, nv); } }));
    }

    private Control BoolRow(string label, List<LevelObject> sel, Func<LevelObject, bool> get,
        Action<LevelObject, bool> set, EditorDocument doc)
    {
        bool mixed = sel.Select(get).Distinct().Count() > 1;
        var check = new CheckBox { IsChecked = mixed ? null : get(sel[0]), IsThreeState = mixed };
        check.IsCheckedChanged += (_, _) =>
        {
            bool val = check.IsChecked ?? false;
            check.IsThreeState = false;
            Commit(doc, () => { foreach (LevelObject o in sel) { set(o, val); } });
        };
        return LabeledRow(label, check);
    }

    // ---- Row builders over raw (model, section) targets ----

    private Control StringRowRaw(string label, IReadOnlyList<(object Model, RflSection Section)> targets,
        Func<object, string> get, Action<object, string> set, EditorDocument doc, string key)
    {
        bool mixed = targets.Select(t => get(t.Model)).Distinct().Count() > 1;
        var box = new TextBox { Text = mixed ? string.Empty : get(targets[0].Model), Watermark = mixed ? Mixed : null, FontSize = 12 };
        box.LostFocus += (_, _) => Commit(doc, () =>
        {
            foreach (var (m, s) in targets)
            {
                doc.EditValue(s, "Edit " + label, get(m), box.Text ?? string.Empty, v => set(m, v));
            }
        });
        return LabeledRow(label, box);
    }

    private Control BoolRowRaw(string label, IReadOnlyList<(object Model, RflSection Section)> targets,
        Func<object, bool> get, Action<object, bool> set, EditorDocument doc, string key)
    {
        bool mixed = targets.Select(t => get(t.Model)).Distinct().Count() > 1;
        var check = new CheckBox { IsChecked = mixed ? null : get(targets[0].Model), IsThreeState = mixed };
        check.IsCheckedChanged += (_, _) =>
        {
            bool val = check.IsChecked ?? false;
            check.IsThreeState = false;
            Commit(doc, () =>
            {
                foreach (var (m, s) in targets)
                {
                    doc.EditValue(s, "Edit " + label, get(m), val, v => set(m, v));
                }
            });
        };
        return LabeledRow(label, check);
    }

    private Control Vec3RowRaw(string label, IReadOnlyList<(object Model, RflSection Section)> targets,
        Func<object, Vec3> get, Action<object, Vec3> set, EditorDocument doc, string key)
    {
        bool mixed = targets.Select(t => get(t.Model)).Distinct().Count() > 1;
        return Vec3Editor(label, get(targets[0].Model), mixed, nv => Commit(doc, () =>
        {
            foreach (var (m, s) in targets)
            {
                doc.EditValue(s, "Edit " + label, get(m), nv, v => set(m, v), $"{key}-{s.GetHashCode()}");
            }
        }));
    }

    private Control NumberRow(string label, IReadOnlyList<(object Model, RflSection Section)> targets,
        PropertyInfo prop, Type t, EditorDocument doc)
    {
        object?[] values = targets.Select(x => prop.GetValue(x.Model)).ToArray();
        bool mixed = values.Select(v => v?.ToString()).Distinct().Count() > 1;
        var box = new TextBox
        {
            Text = mixed ? string.Empty : Convert.ToString(values[0], CultureInfo.InvariantCulture),
            Watermark = mixed ? Mixed : null,
            FontSize = 12,
        };
        box.LostFocus += (_, _) =>
        {
            if (!TryParseNumber(box.Text, t, out object? parsed))
            {
                return;
            }

            Commit(doc, () =>
            {
                foreach (var (m, s) in targets)
                {
                    object? old = prop.GetValue(m);
                    doc.EditValue(s, "Edit " + label, old, parsed, v => prop.SetValue(m, v));
                }
            });
        };
        return LabeledRow(label, box);
    }

    private Control EnumRow(string label, IReadOnlyList<(object Model, RflSection Section)> targets,
        PropertyInfo prop, Type t, EditorDocument doc)
    {
        var combo = new ComboBox { ItemsSource = Enum.GetValues(t), FontSize = 12, MinWidth = 120 };
        object?[] values = targets.Select(x => prop.GetValue(x.Model)).ToArray();
        bool mixed = values.Select(v => v?.ToString()).Distinct().Count() > 1;
        combo.SelectedItem = mixed ? null : values[0];
        combo.SelectionChanged += (_, _) =>
        {
            object? val = combo.SelectedItem;
            if (val is null)
            {
                return;
            }

            Commit(doc, () =>
            {
                foreach (var (m, s) in targets)
                {
                    doc.EditValue(s, "Edit " + label, prop.GetValue(m), val, v => prop.SetValue(m, v));
                }
            });
        };
        return LabeledRow(label, combo);
    }

    // ---- Editor primitives ----

    private Control Vec3Editor(string label, Vec3 v, bool mixed, Action<Vec3> apply)
    {
        TextBox X = NumBox(mixed ? string.Empty : F(v.X));
        TextBox Y = NumBox(mixed ? string.Empty : F(v.Y));
        TextBox Z = NumBox(mixed ? string.Empty : F(v.Z));

        void Commit()
        {
            if (float.TryParse(X.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(Y.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(Z.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                apply(new Vec3(x, y, z));
            }
        }

        X.LostFocus += (_, _) => Commit();
        Y.LostFocus += (_, _) => Commit();
        Z.LostFocus += (_, _) => Commit();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        row.Children.Add(X);
        row.Children.Add(Y);
        row.Children.Add(Z);
        return LabeledRow(label, row);
    }

    private void Commit(EditorDocument doc, Action apply)
    {
        using (doc.Undo.BeginTransaction("Edit properties"))
        {
            apply();
        }

        _host?.RefreshSelectionOverlay();
        _host?.Dispatcher.ShowMessage("Edited");
    }

    private static Control LabeledRow(string label, Control editor)
    {
        var grid = new Grid { Margin = new Avalonia.Thickness(0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(120)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        // Field labels wrap rather than clip when they exceed the 120px column (Task 1f).
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 6, 0),
            Opacity = 0.8,
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(lbl);
        grid.Children.Add(editor);
        return grid;
    }

    private static Control ReadonlyRow(string label, string value) =>
        LabeledRow(label, new TextBlock { Text = value, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

    private static TextBox NumBox(string text) => new() { Text = text, FontSize = 12, Width = 64 };

    private static Control Header(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Avalonia.Thickness(0, 2, 0, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    private void ShowEmpty(string message) => SetContent(new TextBlock
    {
        Text = message,
        Opacity = 0.6,
        Margin = new Avalonia.Thickness(4),
    });

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryParseNumber(string? text, Type t, out object? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            if (t == typeof(float)) { value = float.Parse(text, CultureInfo.InvariantCulture); }
            else if (t == typeof(double)) { value = double.Parse(text, CultureInfo.InvariantCulture); }
            else if (t == typeof(int)) { value = int.Parse(text, CultureInfo.InvariantCulture); }
            else if (t == typeof(uint)) { value = uint.Parse(text, CultureInfo.InvariantCulture); }
            else if (t == typeof(short)) { value = short.Parse(text, CultureInfo.InvariantCulture); }
            else if (t == typeof(ushort)) { value = ushort.Parse(text, CultureInfo.InvariantCulture); }
            else if (t == typeof(byte)) { value = byte.Parse(text, CultureInfo.InvariantCulture); }
            else if (t == typeof(sbyte)) { value = sbyte.Parse(text, CultureInfo.InvariantCulture); }
            else if (t == typeof(long)) { value = long.Parse(text, CultureInfo.InvariantCulture); }
            else { return false; }
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string Humanize(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                sb.Append(' ');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.App.Panels;

/// <summary>What the keyframe panel asks of the editor around it (all optional; tests leave most unset).</summary>
internal sealed class MoverPanelServices
{
    /// <summary>Selects a keyframe object in the viewport (the ◀ ▶ steps).</summary>
    public Action<LevelObject>? SelectObject { get; set; }

    /// <summary>Plays a sound file preview; false when it cannot.</summary>
    public Func<string, bool>? PlaySound { get; set; }

    /// <summary>Stops any sound preview.</summary>
    public Action? StopSound { get; set; }

    /// <summary>Opens a sound picker and returns the chosen file name, or null.</summary>
    public Func<Task<string?>>? PickSoundFile { get; set; }

    /// <summary>The viewport mover preview.</summary>
    public MoverPreviewController? Preview { get; set; }

    /// <summary>Called after every document edit the panel makes (redraws paths and overlays).</summary>
    public Action? AfterEdit { get; set; }

    /// <summary>Shows a status-bar message.</summary>
    public Action<string>? ShowMessage { get; set; }
}

/// <summary>
/// The docked Keyframe panel: RED's Keyframe Properties dialog (owner's RED screenshot; Alpine RED's layout,
/// editor_patch/resources.rc:1395-1470 and mover_path.cpp:1604-1747) split in two:
/// <list type="number">
/// <item><b>Mover (whole chain)</b> — RED's "Keyframe List Properties": the mover flags, Movement Type, Starting
/// Keyframe with Preview, the four sounds (Browse…, Volume, Preview), and Propagate Sounds / Propagate Properties /
/// Stop Sounds.</item>
/// <item><b>This keyframe (n of N)</b> — RED's "Keyframe Properties" with ◀ ▶ to step through the chain: timing,
/// trigger event and item UIDs, Rotation Properties, the Alpine Path group and Riders, greyed as Alpine RED greys
/// them (mover_path.cpp:673-689).</item>
/// </list>
/// Several selected keyframes of one mover edit together: a field whose values differ shows blank and is left as it
/// is unless edited. Every edit is one undo step, through the same accessors as the Lua API.
/// </summary>
internal sealed class MoverKeyframePanel : UserControl
{
    private readonly EditorDocument _doc;
    private readonly MoverService _movers;
    private readonly Group _group;
    private readonly MoverPanelServices _services;
    private readonly List<Keyframe> _selected;
    private readonly List<Action> _enableUpdates = new();
    private int _shown;
    private bool _building;
    private StackPanel? _transport;

    /// <param name="selected">The selected keyframes of this mover (empty when a member brush or object is selected).</param>
    public MoverKeyframePanel(EditorDocument doc, Group group, IReadOnlyList<Keyframe> selected, MoverPanelServices? services = null)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _group = group ?? throw new ArgumentNullException(nameof(group));
        _movers = new MoverService(doc);
        _services = services ?? new MoverPanelServices();
        MovingGroupData data = group.MovingData ?? throw new ArgumentException("Not a mover.", nameof(group));
        _selected = selected.Where(k => data.Keyframes.Contains(k)).ToList();
        _shown = _selected.Count > 0
            ? data.Keyframes.IndexOf(_selected[0])
            : Math.Clamp(data.StartingKeyframe, 0, Math.Max(0, data.Keyframes.Count - 1));
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        Rebuild();
    }

    /// <summary>The mover this panel edits.</summary>
    public Group Mover => _group;

    /// <summary>The keyframes the "This keyframe" part edits (the selection, or the shown one).</summary>
    public IReadOnlyList<Keyframe> Editing => _selected.Count > 1 ? _selected : Current is { } k ? new[] { k } : Array.Empty<Keyframe>();

    /// <summary>The 0-based index of the keyframe shown in the "This keyframe" part.</summary>
    public int ShownIndex => _shown;

    private MovingGroupData Data => _group.MovingData!;

    private Keyframe? Current => _shown >= 0 && _shown < Data.Keyframes.Count ? Data.Keyframes[_shown] : null;

    private bool Legacy
    {
        get
        {
            foreach (var s in _doc.Rfl.Sections)
            {
                if (s.Content is AlpineLevelPropertiesSection p)
                {
                    return p.LegacyMovers != 0;
                }
            }

            return true;
        }
    }

    /// <summary>Shows keyframe <paramref name="index"/> (0-based) and selects it in the viewport.</summary>
    public void Step(int delta)
    {
        int count = Data.Keyframes.Count;
        if (count == 0)
        {
            return;
        }

        _shown = ((_shown + delta) % count + count) % count;
        _selected.Clear();
        Keyframe k = Data.Keyframes[_shown];
        _selected.Add(k);
        Rebuild();
        if (_doc.FindByUid(k.Uid) is { } o)
        {
            _services.SelectObject?.Invoke(o);
        }
    }

    /// <summary>Rebuilds every control from the document.</summary>
    public void Rebuild()
    {
        _building = true;
        _enableUpdates.Clear();
        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(BuildChain());
        root.Children.Add(BuildKeyframe());
        Content = root;
        UpdateEnables();
        _building = false;
    }

    // ---- part 1: the whole chain ------------------------------------------------------------------

    private Control BuildChain()
    {
        var body = new StackPanel { Spacing = 4 };

        // Flags in RED's order (resources.rc:1401-1417): Is Door, Starts Moving Backwards, Hold Open / Rotate in Place,
        // Use Travel Time as Velocity, Force Orient / No Player Collide.
        var flags = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
        AddAt(flags, MoverCheck("Is Door", "IsDoor", "is_door"), 0, 0);
        AddAt(flags, MoverCheck("Starts Moving Backwards", "StartsBackwards", "starts_backwards"), 0, 1);
        AddAt(flags, MoverCheck("Hold Open", "HoldOpen", "hold_open"), 1, 0);
        AddAt(flags, MoverCheck("Rotate in Place", "RotateInPlace", "rotate_in_place", rebuildOnChange: true), 1, 1);
        AddAt(flags, MoverCheck("Use Travel Time as Velocity", "UseTravelTimeAsSpeed", "use_travel_time_as_speed"), 2, 0);
        AddAt(flags, MoverCheck("Force Orient", "ForceOrient", "force_orient"), 2, 1);
        AddAt(flags, MoverCheck("No Player Collide", "NoPlayerCollide", "no_player_collide"), 3, 0);
        body.Children.Add(flags);

        var type = new ComboBox { Name = "mover_movement_type", ItemsSource = MoverInspectorSchema.MovementTypes, MinWidth = 140 };
        type.SelectedIndex = Math.Clamp(Data.MovementType - 1, -1, MoverInspectorSchema.MovementTypes.Count - 1);
        type.SelectionChanged += (_, _) =>
        {
            if (!_building && type.SelectedIndex >= 0)
            {
                SetMover(MoverField("MovementType"), type.SelectedIndex);
            }
        };
        body.Children.Add(Row("Movement Type:", type));

        // Starting Keyframe: RED shows it one-based (the RFL stores it zero-based; level.h:1662).
        int count = Math.Max(1, Data.Keyframes.Count);
        var start = new NumericUpDown
        {
            Name = "mover_starting_keyframe", Minimum = 1, Maximum = count, Increment = 1, FormatString = "0", Width = 112,
            Value = Math.Clamp(Data.StartingKeyframe + 1, 1, count),
        };
        start.ValueChanged += (_, _) =>
        {
            if (!_building && start.Value is decimal v)
            {
                SetMover(MoverField("StartingKeyframe"), (int)v - 1);
            }
        };
        var preview = new Button { Name = "mover_preview", Content = "Preview" };
        ToolTip.SetTip(preview, "Plays the mover in the viewport with its real path and timing.");
        preview.Click += (_, _) => StartPreview();
        body.Children.Add(Row("Starting Keyframe:", Horizontal(start, preview)));

        _transport = new StackPanel { Spacing = 3 };
        body.Children.Add(_transport);
        UpdateTransport();

        
        body.Children.Add(SoundRow("Start Sound:", "StartSound", "StartVol", "start"));
        body.Children.Add(SoundRow("Looping Sound:", "LoopingSound", "LoopingVol", "loop"));
        body.Children.Add(SoundRow("Stop Sound:", "StopSound", "StopVol", "stop"));
        body.Children.Add(SoundRow("Close Sounds:", "CloseSound", "CloseVol", "close"));

        var propSounds = new Button { Name = "mover_propagate_sounds", Content = "Propagate Sounds" };
        ToolTip.SetTip(propSounds, "Copies these four sounds and volumes to every mover with a keyframe of this keyframe's script name (RED).");
        propSounds.Click += (_, _) => Propagate(sounds: true);
        var propProps = new Button { Name = "mover_propagate_properties", Content = "Propagate Properties" };
        ToolTip.SetTip(propProps, "Copies the mover flags, movement type and starting keyframe to every mover with a keyframe of this script name, and this keyframe's timing, UIDs and rotation to every keyframe of that name (RED).");
        propProps.Click += (_, _) => Propagate(sounds: false);
        var stopSounds = new Button { Name = "mover_stop_sounds", Content = "Stop Sounds" };
        ToolTip.SetTip(stopSounds, "Stops the sound preview.");
        stopSounds.Click += (_, _) => _services.StopSound?.Invoke();
        body.Children.Add(new WrapPanel { Children = { Spaced(propSounds), Spaced(propProps), Spaced(stopSounds) } });

        return Group($"Mover (whole chain): {_group.Name}", body);
    }

    private Control MoverCheck(string label, string path, string name, bool rebuildOnChange = false)
    {
        InspectorField field = MoverField(path);
        var cb = new CheckBox { Name = "mover_" + name, Content = label, IsChecked = InspectorAccess.Get(_movers, _group, field) is true };
        cb.IsCheckedChanged += (_, _) =>
        {
            if (_building)
            {
                return;
            }

            SetMover(field, cb.IsChecked == true);
            if (rebuildOnChange)
            {
                UpdateEnables();
            }
        };
        if (path == "RotateInPlace")
        {
            _rotateBox = cb;
        }

        return cb;
    }

    private CheckBox? _rotateBox;

    private Control SoundRow(string label, string soundPath, string volPath, string name)
    {
        InspectorField sound = MoverField(soundPath);
        InspectorField vol = MoverField(volPath);
        var box = new TextBox { Name = "mover_sound_" + name, Text = InspectorAccess.Get(_movers, _group, sound) as string ?? string.Empty, MinWidth = 110 };
        box.LostFocus += (_, _) =>
        {
            if (!_building)
            {
                SetMover(sound, box.Text ?? string.Empty);
            }
        };
        var browse = new Button { Content = "Browse..." };
        browse.Click += async (_, _) =>
        {
            if (_services.PickSoundFile is { } pick && await pick() is { Length: > 0 } file)
            {
                box.Text = file;
                SetMover(sound, file);
            }
        };
        var volume = new NumericUpDown
        {
            Name = "mover_volume_" + name, Minimum = 0, Maximum = 100, Increment = 0.05m, FormatString = "0.00", Width = 124,
            Value = InspectorAccess.Get(_movers, _group, vol) is float f ? (decimal)f : 0m,
        };
        ToolTip.SetTip(volume, "Volume");
        volume.ValueChanged += (_, _) =>
        {
            if (!_building && volume.Value is decimal v)
            {
                SetMover(vol, (float)v);
            }
        };
        var play = new Button { Content = "Preview" };
        play.Click += (_, _) =>
        {
            string file = box.Text ?? string.Empty;
            if (file.Length == 0)
            {
                return;
            }

            if (_services.PlaySound?.Invoke(file) != true)
            {
                _services.ShowMessage?.Invoke($"Cannot preview {file} (only .wav files found in the mounted game files play).");
            }
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 1) };
        AddAt(grid, Label(label), 0, 0);
        AddAt(grid, box, 0, 1);
        AddAt(grid, Spaced(browse), 0, 2);
        AddAt(grid, Horizontal(new TextBlock { Text = "Volume", FontSize = 11, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center }, volume, play), 1, 1);
        return grid;
    }

    private void StartPreview()
    {
        if (_services.Preview is not { } preview)
        {
            return;
        }

        if (preview.Start(_group) is { } why)
        {
            _services.ShowMessage?.Invoke(why);
        }

        UpdateTransport();
    }

    private void UpdateTransport()
    {
        if (_transport is null)
        {
            return;
        }

        _transport.Children.Clear();
        if (_services.Preview is not { Active: true } preview || !ReferenceEquals(preview.Group, _group))
        {
            return;
        }

        var play = new Button { Name = "preview_play", Content = preview.Playing ? "Pause" : "Play", MinWidth = 60 };
        play.Click += (_, _) => preview.PlayPause();
        var stop = new Button { Name = "preview_stop", Content = "Stop" };
        ToolTip.SetTip(stop, "Stops and returns the mover to its rest pose.");
        stop.Click += (_, _) => preview.Stop();
        var close = new Button { Name = "preview_close", Content = "Close" };
        close.Click += (_, _) => preview.Close();
        var speed = new ComboBox { Name = "preview_speed", ItemsSource = MoverPreviewController.Speeds.Select(s => s.Label).ToList(), Width = 80 };
        speed.SelectedIndex = Math.Max(0, MoverPreviewController.Speeds.ToList().FindIndex(s => s.Speed == preview.Speed));
        speed.SelectionChanged += (_, _) =>
        {
            if (speed.SelectedIndex >= 0)
            {
                preview.Speed = MoverPreviewController.Speeds[speed.SelectedIndex].Speed;
            }
        };
        var scrub = new Slider { Name = "preview_scrub", Minimum = 0, Maximum = 1, Value = preview.Playback?.RunFraction ?? 0, SmallChange = 0.01, LargeChange = 0.1 };
        bool settingScrub = false;
        scrub.ValueChanged += (_, _) =>
        {
            if (!settingScrub)
            {
                preview.Scrub((float)scrub.Value);
            }
        };
        var status = new TextBlock { Name = "preview_status", Text = preview.Status, FontSize = 11, Opacity = 0.8 };
        _transport.Children.Add(new TextBlock { Text = "Mover Preview", FontWeight = FontWeight.SemiBold, FontSize = 12 });
        _transport.Children.Add(new WrapPanel { Children = { Spaced(play), Spaced(stop), Spaced(speed), Spaced(close) } });
        _transport.Children.Add(scrub);
        _transport.Children.Add(status);
        if (Legacy)
        {
            _transport.Children.Add(Note("Legacy movers: straight segments without easing, as the game plays them."));
        }

        _transportSync = () =>
        {
            play.Content = preview.Playing ? "Pause" : "Play";
            settingScrub = true;
            scrub.Value = preview.Playback?.RunFraction ?? 0;
            settingScrub = false;
            status.Text = preview.Status;
        };
    }

    private Action? _transportSync;
    private bool _subscribed;

    private void Subscribe()
    {
        if (!_subscribed && _services.Preview is { } p)
        {
            p.Changed += OnPreviewChanged;
            _subscribed = true;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed && _services.Preview is { } p)
        {
            p.Changed -= OnPreviewChanged;
            _subscribed = false;
        }
    }

    private void OnPreviewChanged()
    {
        bool shownForUs = _transport is { Children.Count: > 0 };
        bool activeForUs = _services.Preview is { Active: true } p && ReferenceEquals(p.Group, _group);
        if (shownForUs != activeForUs)
        {
            UpdateTransport();
            return;
        }

        _transportSync?.Invoke();
    }

    /// <summary>Test hook: re-reads the transport after driving the preview directly.</summary>
    internal void SyncTransport() => OnPreviewChanged();

    private void Propagate(bool sounds)
    {
        if (_selected.Count > 1)
        {
            _services.ShowMessage?.Invoke("You can only propagate from one keyframe!");
            return;
        }

        if (Current is not { } source)
        {
            return;
        }

        MoverPropagationResult r = sounds ? MoverPropagation.PropagateSounds(_movers, source) : MoverPropagation.PropagateProperties(_movers, source);
        if (r.Refusal is { } why)
        {
            _services.ShowMessage?.Invoke(why);
            return;
        }

        _services.ShowMessage?.Invoke(sounds
            ? $"Sounds propagated to {r.Movers} mover(s) with a \"{source.ScriptName}\" keyframe."
            : $"Properties propagated to {r.Movers} mover(s) and {r.Keyframes} \"{source.ScriptName}\" keyframe(s).");
        AfterEdit();
    }

    // ---- part 2: this keyframe ----------------------------------------------------------------------

    private Control BuildKeyframe()
    {
        var body = new StackPanel { Spacing = 4 };
        int count = Data.Keyframes.Count;
        IReadOnlyList<Keyframe> keys = Editing;

        var prev = new Button { Name = "kf_prev", Content = "◀", Padding = new Thickness(8, 2) };
        ToolTip.SetTip(prev, "Previous keyframe in the chain");
        prev.Click += (_, _) => Step(-1);
        var next = new Button { Name = "kf_next", Content = "▶", Padding = new Thickness(8, 2) };
        ToolTip.SetTip(next, "Next keyframe in the chain");
        next.Click += (_, _) => Step(1);
        var add = new Button { Name = "kf_add", Content = "Add Keyframe" };
        ToolTip.SetTip(add, "Adds a keyframe at the mover's centre, selected so you can drag it into place.");
        add.Click += (_, _) => AddKeyframe();
        var delete = new Button { Name = "kf_delete", Content = "Delete" };
        ToolTip.SetTip(delete, "Deletes this keyframe (never the brushes); deleting the last one turns the mover back into static brushes.");
        delete.Click += (_, _) => DeleteKeyframe();
        body.Children.Add(new WrapPanel { Children = { Spaced(prev), Spaced(next), Spaced(add), Spaced(delete) } });

        if (keys.Count == 0)
        {
            body.Children.Add(Note("This mover has no keyframes."));
            return Group("This keyframe", body);
        }

        body.Children.Add(Row("Script Name:", KeyText("ScriptName", "kf_script_name")));
        body.Children.Add(Row("Pause Time:", Seconds(KeyNumber("PauseTime", "kf_pause_time"))));
        body.Children.Add(Row("Departing Travel Time:", Seconds(KeyNumber("DepartTravelTime", "kf_depart_travel_time"))));
        body.Children.Add(Row("Returning Travel Time:", Seconds(KeyNumber("ReturnTravelTime", "kf_return_travel_time"))));
        body.Children.Add(Row("Acceleration Time:", Seconds(KeyNumber("AccelTime", "kf_accel_time"))));
        body.Children.Add(Row("Deceleration Time:", Seconds(KeyNumber("DecelTime", "kf_decel_time"))));
        body.Children.Add(Row("Trigger Event With UID:", KeyUid("EventUid", "kf_event_uid")));
        body.Children.Add(Row("Contains Item UID(s):", new WrapPanel { Children = { Spaced(KeyUid("ItemUid1", "kf_item_uid1")), Spaced(KeyUid("ItemUid2", "kf_item_uid2")) } }));

        body.Children.Add(Group("Rotation Properties", Row("Degrees about Axis:", KeyNumber("DegreesAboutAxis", "kf_degrees_about_axis", 1m, "0.00", -100000, 100000))));

        var path = new StackPanel { Spacing = 3 };
        ComboBox segment = KeyPathCombo("path_segment", "kf_path_segment");
        ComboBox handles = KeyPathCombo("path_tangent", "kf_path_tangent");
        ComboBox easing = KeyPathCombo("path_ease", "kf_path_ease", new[] { "None", "Ease In", "Ease Out", "Ease In and Out" });
        ComboBox facing = MoverPathCombo("path_orientation", "kf_mover_facing");
        ComboBox segFacing = KeyPathCombo("segment_orientation", "kf_segment_facing");
        path.Children.Add(Row("Segment:", segment));
        path.Children.Add(Row("Handles:", handles));
        path.Children.Add(Row("Easing:", easing));
        path.Children.Add(Row("Mover Facing:", facing));
        path.Children.Add(Row("Segment Facing:", segFacing));
        ToolTip.SetTip(facing, "How the whole mover turns as it travels (Alpine).");
        body.Children.Add(Group("Path", path));

        var riders = new StackPanel { Spacing = 2 };
        CheckBox turn = RiderCheck("Turn Riders with Mover", "riders_turn", "kf_riders_turn");
        CheckBox detach = RiderCheck("Detach Riders on Leave", "riders_detach_on_leave", "kf_riders_detach");
        riders.Children.Add(turn);
        riders.Children.Add(detach);
        body.Children.Add(Group("Riders", riders));

        // Alpine RED's enables (mover_path.cpp:673-689): legacy movers grey the Alpine fields; Rotate in Place greys
        // the path shape and facing; a mover without a facing of its own greys Segment Facing.
        _enableUpdates.Add(() =>
        {
            bool legacy = Legacy;
            bool rotating = _rotateBox?.IsChecked == true;
            bool faces = facing.SelectedIndex != 0;
            segment.IsEnabled = !legacy && !rotating;
            handles.IsEnabled = !legacy && !rotating;
            easing.IsEnabled = !legacy;
            facing.IsEnabled = !legacy && !rotating;
            segFacing.IsEnabled = !legacy && !rotating && faces;
            turn.IsEnabled = !legacy;
            detach.IsEnabled = !legacy;
        });
        facing.SelectionChanged += (_, _) => UpdateEnables();

        if (Legacy)
        {
            body.Children.Add(Note("Path and Riders apply with Legacy movers off (Level Properties ▸ Compatibility)."));
        }

        string title = keys.Count > 1
            ? $"These keyframes ({keys.Count} selected of {count})"
            : $"This keyframe ({_shown + 1} of {count})";
        return Group(title, body);
    }

    private void UpdateEnables()
    {
        foreach (Action a in _enableUpdates)
        {
            a();
        }
    }

    private void AddKeyframe()
    {
        Keyframe kf = _movers.AddKeyframe(_group, _movers.MemberBoundsCenter(_group), Mat3.Identity);
        _shown = Data.Keyframes.IndexOf(kf);
        _selected.Clear();
        _selected.Add(kf);
        AfterEdit();
        Rebuild();
        if (_doc.FindByUid(kf.Uid) is { } o)
        {
            _services.SelectObject?.Invoke(o);
        }

        _services.ShowMessage?.Invoke("Keyframe added at the mover's centre and selected: drag it to shape the path.");
    }

    private void DeleteKeyframe()
    {
        if (Current is not { } k)
        {
            return;
        }

        bool last = Data.Keyframes.Count <= 1;
        _movers.RemoveKeyframe(_group, k);
        _selected.Clear();
        AfterEdit();
        if (!last)
        {
            _shown = Math.Clamp(_shown, 0, Data.Keyframes.Count - 1);
            Rebuild();
        }

        _services.ShowMessage?.Invoke(last ? "Last keyframe removed: the mover is static brushes again." : "Keyframe removed.");
    }

    private Control KeyText(string path, string name)
    {
        InspectorField field = KeyField(path);
        IReadOnlyList<Keyframe> keys = Editing;
        string? shared = Shared(keys, k => k.ScriptName);
        var box = new TextBox { Name = name, Text = shared ?? string.Empty, Watermark = shared is null ? "(mixed)" : null, MinWidth = 130 };
        box.LostFocus += (_, _) =>
        {
            string v = box.Text ?? string.Empty;
            if (_building || (shared is null && v.Length == 0))
            {
                return;
            }

            SetKeys(field, v);
        };
        return box;
    }

    private Control KeyNumber(string path, string name, decimal increment = 0.05m, string format = "0.00", decimal min = 0, decimal max = 100000)
    {
        InspectorField field = KeyField(path);
        IReadOnlyList<Keyframe> keys = Editing;
        float? shared = SharedValue(keys, k => field.Get(k) is float f ? f : 0f);
        var box = new NumericUpDown
        {
            Name = name, Minimum = min, Maximum = max, Increment = increment, FormatString = format, Width = 128,
            Value = shared is float f ? (decimal)f : null,
        };
        box.ValueChanged += (_, _) =>
        {
            if (!_building && box.Value is decimal v)
            {
                SetKeys(field, (float)v);
            }
        };
        return box;
    }

    /// <summary>A UID field: blank for "none" (-1), as RED shows it.</summary>
    private Control KeyUid(string path, string name)
    {
        InspectorField field = KeyField(path);
        IReadOnlyList<Keyframe> keys = Editing;
        int? shared = SharedValue(keys, k => field.Get(k) is int i ? i : -1);
        var box = new NumericUpDown
        {
            Name = name, Minimum = -1, Maximum = int.MaxValue, Increment = 1, FormatString = "0", Width = 118,
            Value = shared is int v && v >= 0 ? v : null,
        };
        ToolTip.SetTip(box, "Blank = none");
        box.ValueChanged += (_, _) =>
        {
            if (_building)
            {
                return;
            }

            SetKeys(field, box.Value is decimal d && d >= 0 ? (int)d : -1);
        };
        return box;
    }

    private ComboBox KeyPathCombo(string key, string name, IReadOnlyList<string>? labels = null)
    {
        InspectorField field = MoverInspectorSchema.KeyframePathFields.First(f => f.EffectiveKey == key);
        IReadOnlyList<Keyframe> keys = Editing;
        int? shared = SharedValue(keys, k => _doc.FindByUid(k.Uid) is { } o && InspectorAccess.Get(_doc, o, field) is int i ? i : 0);
        var combo = new ComboBox { Name = name, ItemsSource = labels ?? field.Options, MinWidth = 140, SelectedIndex = shared ?? -1 };
        combo.SelectionChanged += (_, _) =>
        {
            if (!_building && combo.SelectedIndex >= 0)
            {
                SetKeys(field, combo.SelectedIndex);
            }
        };
        return combo;
    }

    private ComboBox MoverPathCombo(string key, string name)
    {
        InspectorField field = MoverInspectorSchema.MoverPathFields.First(f => f.EffectiveKey == key);
        var combo = new ComboBox
        {
            Name = name, ItemsSource = field.Options, MinWidth = 140,
            SelectedIndex = InspectorAccess.Get(_movers, _group, field) is int i ? i : 0,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (!_building && combo.SelectedIndex >= 0)
            {
                SetMover(field, combo.SelectedIndex);
            }
        };
        return combo;
    }

    private CheckBox RiderCheck(string label, string key, string name)
    {
        InspectorField field = MoverInspectorSchema.MoverPathFields.First(f => f.EffectiveKey == key);
        var cb = new CheckBox { Name = name, Content = label, IsChecked = InspectorAccess.Get(_movers, _group, field) is true };
        ToolTip.SetTip(cb, "Applies to the whole mover (Alpine).");
        cb.IsCheckedChanged += (_, _) =>
        {
            if (!_building)
            {
                SetMover(field, cb.IsChecked == true);
            }
        };
        return cb;
    }

    // ---- writes (one undo step each) -----------------------------------------------------------------

    private void SetMover(InspectorField field, object? value)
    {
        if (Equals(InspectorAccess.Get(_movers, _group, field), value))
        {
            return; // nothing changes (a focus change on an untouched field)
        }

        try
        {
            InspectorAccess.Set(_movers, _group, field, value);
        }
        catch (InvalidOperationException ex)
        {
            _services.ShowMessage?.Invoke(ex.Message);
        }

        AfterEdit();
    }

    private void SetKeys(InspectorField field, object? value)
    {
        IReadOnlyList<Keyframe> keys = Editing;
        if (keys.All(k => _doc.FindByUid(k.Uid) is not { } o || Equals(InspectorAccess.Get(_doc, o, field), value)))
        {
            return; // nothing changes (a focus change on an untouched field)
        }

        try
        {
            using (_doc.Undo.BeginTransaction("Edit " + field.Label))
            {
                foreach (Keyframe k in keys)
                {
                    if (_doc.FindByUid(k.Uid) is { } o)
                    {
                        InspectorAccess.Set(_doc, o, field, value, _movers);
                    }
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            _services.ShowMessage?.Invoke(ex.Message);
        }

        AfterEdit();
    }

    private void AfterEdit()
    {
        _services.Preview?.Refresh();
        _services.AfterEdit?.Invoke();
    }

    private static InspectorField KeyField(string path) => MoverInspectorSchema.KeyframeFields.First(f => f.Path == path);

    private static InspectorField MoverField(string path) => MoverInspectorSchema.MoverFields.First(f => f.Path == path);

    private static string? Shared(IReadOnlyList<Keyframe> keys, Func<Keyframe, string> get)
    {
        string? v = null;
        for (int i = 0; i < keys.Count; i++)
        {
            string s = get(keys[i]);
            if (i > 0 && !string.Equals(s, v, StringComparison.Ordinal))
            {
                return null;
            }

            v = s;
        }

        return v;
    }

    private static T? SharedValue<T>(IReadOnlyList<Keyframe> keys, Func<Keyframe, T> get)
        where T : struct
    {
        T? v = null;
        for (int i = 0; i < keys.Count; i++)
        {
            T s = get(keys[i]);
            if (i > 0 && !EqualityComparer<T>.Default.Equals(s, v!.Value))
            {
                return null;
            }

            v = s;
        }

        return v;
    }

    // ---- layout helpers ------------------------------------------------------------------------------

    private static Control Group(string title, Control body) => new Border
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 4, 6, 6),
        Child = new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12 }, body },
        },
    };

    private static Control Row(string label, Control editor)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("136,*") };
        TextBlock l = Label(label);
        grid.Children.Add(l);
        Grid.SetColumn(editor, 1);
        editor.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(editor);
        return grid;
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private static Control Seconds(Control editor) => Horizontal(editor, new TextBlock { Text = "Seconds", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

    private static StackPanel Horizontal(params Control[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (Control c in items)
        {
            p.Children.Add(c);
        }

        return p;
    }

    private static Control Spaced(Control c)
    {
        c.Margin = new Thickness(0, 0, 4, 2);
        return c;
    }

    private static void AddAt(Grid grid, Control c, int row, int col)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, col);
        grid.Children.Add(c);
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    /// <summary>The panel's numbers as invariant text (test helper).</summary>
    internal static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

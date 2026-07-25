using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Input;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Regression for the trigger MP-flag data-loss defect (alpine-gap-inventory item #1): toggling a
/// Solo / Clientside / Solo-Ignore-Resets checkbox on a trigger that carries a real script name used
/// to overwrite the whole 0xAB-encoded name with just the flag byte, discarding the script name.
/// Alpine's <c>PutFlagsIntoScriptName</c> (trigger.cpp:64-72) keeps <c>prefix + flags + name</c>;
/// the fix retains everything after the flag byte on re-encode. These tests drive the real
/// Properties-panel checkbox and assert the script name round-trips through the toggle (and undo).
/// </summary>
public sealed class TriggerMpFlagTests
{
    private const char Prefix = '«'; // 0xAB PF-flags prefix
    private const int Solo = 0x4;
    private const int Clientside = 0x2;

    private sealed class FakeHost : IEditorHost
    {
        private readonly EditorSession _s;
        private readonly CommandDispatcher _dispatcher =
            new(CommandCatalog.BuildRegistry(), Keymap.FromPreset(CommandCatalog.RedClassic));

        public FakeHost(EditorSession s) => _s = s;

        public EditorDocument? Document => _s.Document;
        public BrushEditor? BrushEditor => _s.BrushEditor;
        public SelectionRouter Selection => _s.Selection;
        public CommandDispatcher Dispatcher => _dispatcher;
        public void RequestSceneRebuild() { }

        public void RequestHistoryJump(Ged.Core.Editor.UndoNode target) { }
        public void RefreshSelectionOverlay() { }
        public void FrameObject(LevelObject o) { }
        public void FrameBrush(int uid) { }
        public void FocusTextureTools() { }
        public void ArmTextureEyedropper(Action<string> onSampled) { }
        public void ViewFromObject(LevelObject o) { }
        public int? SelectedAnnotationId => null;
        public void SelectAnnotation(int? id) { }
        public void DeleteAnnotation(int id) { }
        public Vec3 PlacementPoint => default;
        public LinkService? Links => null;
        public PrefabInstanceService? PrefabInstances => null;
        public string? GetLightCookie(int lightUid) => null;
        public float GetLightCookieSharpness(int lightUid) => 1f;
        public void SetLightCookie(int lightUid, string? cookieFile) { }
        public void SetLightCookieSharpness(int lightUid, float sharpness) { }
        public Task<string?> PickCookieImageAsync() => Task.FromResult<string?>(null);
        public void OrphanPrefabInstance(int instanceId) { }
        public void SelectPrefabInstanceMembers(int instanceId) { }
        public void OnObjectPlaced(LevelObject? placed) { }
        public void PlaceFromPalette(LevelObjectKind kind, string? className) { }
        public void MovePlayerStartHere() { }
        public void PlaceEventFromPalette(Ged.Core.Tables.EventSchema schema) { }
        public IReadOnlyList<string> ClassNamesFor(LevelObjectKind kind) => Array.Empty<string>();
        public PaletteCategoryNode ClutterCategoryTree() => PaletteCategoryNode.Empty;
        public PaletteCategoryNode EntityCategoryTree() => PaletteCategoryNode.Empty;
        public bool PlaySoundPreview(string fileName) => false;
        public void StopSoundPreview() { }
        public IReadOnlyList<string> ClutterSkins(string className) => Array.Empty<string>();
        public void LoadClassThumbnail(LevelObjectKind kind, string? className, Image img) { }
        public string LevelLabel => "test";
        public Task<Ged.Core.Packaging.DependencyScanResult?> ScanDependenciesAsync() => Task.FromResult<Ged.Core.Packaging.DependencyScanResult?>(null);
        public Ged.Core.Packaging.PackfileBuildPlan? CreatePackfilePlan(Ged.Core.Packaging.DependencyScanResult scan) => null;
        public Task OpenPackfileAsync(Ged.Core.Packaging.PackfileBuildPlan plan) => Task.CompletedTask;
    }

    private static (EditorSession Session, PropertiesPanel Panel, Trigger Trigger) SetUp(string scriptName)
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;

        LevelObject trigObj = doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 0))!;
        var trigger = (Trigger)trigObj.Model;
        trigger.ScriptName = scriptName; // test setup: seed the encoded / plain name directly

        session.ActiveSelectKinds = SelectKinds.Objects;
        session.Selection.SelectObject(trigObj);

        var panel = new PropertiesPanel();
        panel.Bind(new FakeHost(session));
        panel.Refresh();
        return (session, panel, trigger);
    }

    private static Control Root(PropertiesPanel panel)
    {
        var scroll = (ScrollViewer)typeof(PropertiesPanel)
            .GetField("_scroll", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        return (Control)scroll.Content!;
    }

    private static IEnumerable<Control> Walk(Control? c)
    {
        if (c is null)
        {
            yield break;
        }

        yield return c;
        switch (c)
        {
            case Panel p:
                foreach (Control child in p.Children.OfType<Control>())
                {
                    foreach (Control d in Walk(child))
                    {
                        yield return d;
                    }
                }

                break;
            case Decorator dec:
                foreach (Control d in Walk(dec.Child))
                {
                    yield return d;
                }

                break;
            case ContentControl cc when cc.Content is Control inner:
                foreach (Control d in Walk(inner))
                {
                    yield return d;
                }

                break;
            case ContentPresenter cp when cp.Content is Control inner:
                foreach (Control d in Walk(inner))
                {
                    yield return d;
                }

                break;
        }
    }

    private static CheckBox Check(PropertiesPanel panel, string label) =>
        (CheckBox)Walk(Root(panel)).OfType<Grid>()
            .Where(g => g.Children.Count >= 2 && g.Children[0] is TextBlock tb && tb.Text == label)
            .Select(g => g.Children[1])
            .First();

    private static TextBox TextBoxFor(PropertiesPanel panel, string label) =>
        (TextBox)Walk(Root(panel)).OfType<Grid>()
            .Where(g => g.Children.Count >= 2 && g.Children[0] is TextBlock tb && tb.Text == label)
            .Select(g => g.Children[1])
            .First();

    private static void Commit(TextBox box, string text)
    {
        box.Text = text;
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent)); // fires the real commit handler
    }

    [AvaloniaFact]
    public void Toggling_A_Second_Flag_Preserves_The_Script_Name_And_Undo_Restores_It()
    {
        // A trigger with a real script name AND the Solo flag already encoded.
        var (session, panel, trig) = SetUp($"{Prefix}{(char)Solo}door_trigger");
        Assert.True(trig.PfSolo);
        Assert.False(trig.PfClientside);

        // Turn Clientside ON via the panel checkbox (fires the real toggle handler).
        Check(panel, "MP Clientside").IsChecked = true;

        // Both flags set AND the script name survives after the flag byte (the defect: it was dropped).
        Assert.True(trig.IsPureFactionEncoded);
        Assert.Equal((char)(Solo | Clientside), trig.ScriptName[1]);
        Assert.Equal("door_trigger", trig.ScriptName[2..]);
        Assert.True(trig.PfSolo);
        Assert.True(trig.PfClientside);

        // Undo restores the exact prior encoded name, name intact.
        session.Document!.Undo.Undo();
        Assert.Equal($"{Prefix}{(char)Solo}door_trigger", trig.ScriptName);
        Assert.Equal("door_trigger", trig.ScriptName[2..]);
        Assert.True(trig.PfSolo);
        Assert.False(trig.PfClientside);
    }

    [AvaloniaFact]
    public void Adding_A_Flag_To_A_Plain_Named_Trigger_Keeps_The_Name()
    {
        var (_, panel, trig) = SetUp("plain_name");
        Assert.False(trig.IsPureFactionEncoded);

        Check(panel, "MP Solo").IsChecked = true;

        Assert.True(trig.IsPureFactionEncoded);
        Assert.Equal((char)Solo, trig.ScriptName[1]);
        Assert.Equal("plain_name", trig.ScriptName[2..]); // name not lost when flags were added
        Assert.True(trig.PfSolo);
    }

    [AvaloniaFact]
    public void Clearing_The_Last_Flag_Leaves_The_Bare_Name_Not_An_Empty_String()
    {
        // Only Clientside encoded over "gate": clearing it must yield the plain name, not "".
        var (session, panel, trig) = SetUp($"{Prefix}{(char)Clientside}gate");
        Assert.True(trig.PfClientside);

        Check(panel, "MP Clientside").IsChecked = false;

        Assert.Equal("gate", trig.ScriptName); // was wiped to string.Empty before the fix
        Assert.False(trig.IsPureFactionEncoded);

        session.Document!.Undo.Undo();
        Assert.Equal($"{Prefix}{(char)Clientside}gate", trig.ScriptName);
        Assert.True(trig.PfClientside);
    }

    [AvaloniaFact]
    public void Amending_A_Numeric_Trigger_Parameter_Commits_And_Is_Undoable()
    {
        // The tester's report: "whenever I try to amend a trigger's parameters it's crashing."
        // Sphere Radius / Team are Nullable<T> fields; committing an edit used to throw
        // InvalidCastException from Convert.ChangeType and take the app down. It must now
        // commit end-to-end through the real Properties-panel text box and be undoable.
        var (session, panel, trig) = SetUp("radius_trigger");
        trig.Shape = Trigger.ShapeSphere;
        trig.SphereRadius = 10f;
        trig.Team = 1;
        panel.Refresh();

        Commit(TextBoxFor(panel, "Sphere Radius"), "5");
        Assert.Equal(5f, trig.SphereRadius);

        Commit(TextBoxFor(panel, "Team"), "2");
        Assert.Equal(2, trig.Team);

        // Each amendment is one undo step; undo restores the prior value.
        session.Document!.Undo.Undo();
        Assert.Equal(1, trig.Team);
        session.Document.Undo.Undo();
        Assert.Equal(10f, trig.SphereRadius);
    }

    [AvaloniaFact]
    public void Hostile_Numeric_Input_On_A_Trigger_Never_Throws()
    {
        // Mid-typing / pasted junk in a numeric editor must not crash the editor. The commit
        // fires on focus-loss; a value that can't be parsed or can't fit the field simply
        // reverts/clamps — it never escapes to the dispatcher.
        var (_, panel, trig) = SetUp("hostile_trigger");
        trig.Shape = Trigger.ShapeSphere;
        trig.SphereRadius = 10f;
        panel.Refresh();

        TextBox radius = TextBoxFor(panel, "Sphere Radius");
        foreach (string hostile in new[]
                 {
                     string.Empty, "-", ".", "1.", "abc", "   ", "1e30",
                     "99999999999999999999999999999999", "3,14", "\t", "NaN", "-.",
                 })
        {
            Commit(radius, hostile); // must not throw
        }

        // The editor still works after the hostile sweep: a real value commits.
        Commit(radius, "7");
        Assert.Equal(7f, trig.SphereRadius);
    }
}

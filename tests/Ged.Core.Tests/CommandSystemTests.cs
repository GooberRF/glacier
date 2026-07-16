using System.Linq;
using Ged.Core.Input;
using Xunit;

namespace Ged.Core.Tests;

public sealed class CommandSystemTests
{
    [Fact]
    public void Registry_Contains_Every_Catalog_Command()
    {
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Assert.Equal(CommandCatalog.All.Count, registry.Commands.Count);
        Assert.True(registry.Contains(CommandIds.SelectInvert));
        Assert.True(registry.Contains(CommandIds.AppCommandPalette));
    }

    [Fact]
    public void Catalog_Has_No_Unimplemented_Commands()
    {
        // Enumerate every catalog entry still flagged unimplemented (id / name /
        // category) so a regression names the offenders directly. The end state is
        // zero: the dispatcher's "not available" toast is now dead code,
        // retained only as a safety net.
        var pending = CommandCatalog.All
            .Where(c => !c.Implemented)
            .Select(c => $"{c.Id} ({c.Name}) [{c.Category}]")
            .ToList();

        Assert.True(
            pending.Count == 0,
            "Every catalog command must be implemented; still unimplemented:\n  " + string.Join("\n  ", pending));

        // The registry mirrors the catalog flag.
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Assert.DoesNotContain(registry.Commands, c => !c.Implemented);
    }

    [Fact]
    public void Registry_Rejects_Duplicate_Id()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition { Id = "x", DisplayName = "X", Category = "C" });
        Assert.Throws<System.InvalidOperationException>(() =>
            registry.Register(new CommandDefinition { Id = "x", DisplayName = "X2", Category = "C" }));
    }

    [Fact]
    public void Red_Classic_Preset_Has_Full_Stock_Bindings()
    {
        Keymap map = Keymap.FromPreset(CommandCatalog.RedClassic);
        Assert.Equal(new KeyGesture("B", GestureModifiers.Shift), map.Resolve(CommandIds.ModeBrush));
        Assert.Equal(new KeyGesture("I"), map.Resolve(CommandIds.SelectInvert));
        Assert.Equal(new KeyGesture("U"), map.Resolve(CommandIds.SelectByUid));
        Assert.Equal(new KeyGesture("H"), map.Resolve(CommandIds.VisHideSelected));
        Assert.Equal(new KeyGesture("H", GestureModifiers.Ctrl), map.Resolve(CommandIds.VisInvertHidden));
        Assert.Equal(new KeyGesture("Tab"), map.Resolve(CommandIds.ViewMaximize)); // TAB is the sole maximize toggle
        Assert.Equal(new KeyGesture("Home"), map.Resolve(CommandIds.CameraGotoPlayerStart));
        Assert.Equal(new KeyGesture("Numpad8"), map.Resolve(CommandIds.CameraPitchUp));
        Assert.Equal(new KeyGesture("\\"), map.Resolve(CommandIds.GridBrightness));
    }

    [Fact]
    public void Play_In_Multi_Binds_F9_And_F10_In_Both_Presets()
    {
        // Alpine muscle memory (the project owner authored Alpine): F9 = Play in Multi,
        // F10 = Play in Multi from Camera, in BOTH presets. Open Dialogue Text is
        // menu-only (its old F9 binding was removed), so there is no F9 conflict.
        foreach (string preset in new[] { CommandCatalog.RedClassic, CommandCatalog.Modern })
        {
            Keymap map = Keymap.FromPreset(preset);
            Assert.Equal(new KeyGesture("F9"), map.Resolve(CommandIds.FilePlayMulti));
            Assert.Equal(new KeyGesture("F10"), map.Resolve(CommandIds.FilePlayMultiFromCamera));
            Assert.Null(map.Resolve(CommandIds.FileDialogueText));
        }
    }

    [Fact]
    public void Tab_Toggles_Maximize_In_Both_Presets_And_F4_F5_Are_Free()
    {
        // Regression: TAB maximize/restore was bound only in the Modern preset, so the
        // default (RED Classic) had it on F4 and TAB fell through to focus traversal.
        // TAB is now the sole toggle in BOTH presets; F4/F5 are unbound, and Reset
        // Viewport Layout stays a command (menu/palette) with no default key.
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        foreach (string preset in new[] { CommandCatalog.RedClassic, CommandCatalog.Modern })
        {
            Keymap map = Keymap.FromPreset(preset);
            Assert.Equal(new KeyGesture("Tab"), map.Resolve(CommandIds.ViewMaximize));

            // TAB dispatches to maximize in the viewport (and every) scope — it is Global.
            Assert.Contains(CommandIds.ViewMaximize, map.Match(new KeyGesture("Tab"), CommandScope.Object, registry));

            // Reset Viewport Layout is reachable but unbound; F4/F5 no longer trigger either.
            Assert.Null(map.Resolve(CommandIds.ViewResetLayout));
            Assert.DoesNotContain(CommandIds.ViewMaximize, map.Match(new KeyGesture("F4"), CommandScope.Global, registry));
            Assert.DoesNotContain(CommandIds.ViewResetLayout, map.Match(new KeyGesture("F5"), CommandScope.Global, registry));
        }
    }

    [Fact]
    public void Modern_Preset_Differs_From_Red()
    {
        Keymap modern = Keymap.FromPreset(CommandCatalog.Modern);
        Assert.Equal(new KeyGesture("Tab"), modern.Resolve(CommandIds.ViewMaximize));
        Assert.Equal(new KeyGesture("1"), modern.Resolve(CommandIds.ModeBrush));
        // Modern leaves some stock-only camera keys unbound.
        Assert.Null(modern.Resolve(CommandIds.CameraPitchUp));
    }

    [Fact]
    public void Shipped_Presets_Are_Conflict_Free()
    {
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Assert.Empty(Keymap.FromPreset(CommandCatalog.RedClassic).FindConflicts(registry));
        Assert.Empty(Keymap.FromPreset(CommandCatalog.Modern).FindConflicts(registry));
    }

    [Fact]
    public void Conflict_Detection_Flags_Two_Global_Commands_On_One_Gesture()
    {
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Keymap map = Keymap.FromPreset(CommandCatalog.RedClassic);

        // Rebind "Select All" onto Ctrl+S which is already "Save" (both Global).
        map.Rebind(CommandIds.SelectAll, new KeyGesture("S", GestureModifiers.Ctrl));

        var conflicts = map.FindConflicts(registry);
        KeyConflict? c = conflicts.FirstOrDefault(k => k.Gesture.Equals(new KeyGesture("S", GestureModifiers.Ctrl)));
        Assert.NotNull(c);
        Assert.Contains(CommandIds.FileSave, c!.CommandIds);
        Assert.Contains(CommandIds.SelectAll, c.CommandIds);
    }

    [Fact]
    public void Mode_Scoped_Bindings_On_Same_Gesture_Do_Not_Conflict()
    {
        // Brush "B" (snap cutter) and Face "B" (bevel) share a gesture but not a scope.
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Keymap map = Keymap.FromPreset(CommandCatalog.RedClassic);
        Assert.DoesNotContain(map.FindConflicts(registry), k => k.Gesture.Equals(new KeyGesture("B")));
    }

    [Fact]
    public void Override_Wins_And_Reset_Reverts()
    {
        Keymap map = Keymap.FromPreset(CommandCatalog.RedClassic);
        var custom = new KeyGesture("J", GestureModifiers.Ctrl);
        map.Rebind(CommandIds.SelectInvert, custom);
        Assert.Equal(custom, map.Resolve(CommandIds.SelectInvert));
        Assert.True(map.IsOverridden(CommandIds.SelectInvert));

        map.ResetBinding(CommandIds.SelectInvert);
        Assert.Equal(new KeyGesture("I"), map.Resolve(CommandIds.SelectInvert));
        Assert.False(map.IsOverridden(CommandIds.SelectInvert));
    }

    [Fact]
    public void Explicit_Unbind_Override_Resolves_To_Null()
    {
        Keymap map = Keymap.FromPreset(CommandCatalog.RedClassic);
        map.Rebind(CommandIds.SelectByUid, null);
        Assert.Null(map.Resolve(CommandIds.SelectByUid));
    }

    [Fact]
    public void Match_Respects_Scope()
    {
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Keymap map = Keymap.FromPreset(CommandCatalog.RedClassic);

        // "I" is global-invert; matches in any active scope.
        Assert.Contains(CommandIds.SelectInvert, map.Match(new KeyGesture("I"), CommandScope.Object, registry));

        // "B" in Brush mode → snap cutter, not face bevel.
        var brushMatches = map.Match(new KeyGesture("B"), CommandScope.Brush, registry);
        Assert.Contains(CommandIds.BrushSnapCutter, brushMatches);
        Assert.DoesNotContain(CommandIds.FaceBevel, brushMatches);
    }

    [Fact]
    public void Texture_Workflow_Hotkeys_Reach_Face_Mode_Too()
    {
        // Item 0h: the texture workflow merged into Face mode, so Shift+D (Select Same Texture)
        // and Shift+S (Grow) both fire in Face mode — the stock texture-workflow selection keys.
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Keymap map = Keymap.FromPreset(CommandCatalog.RedClassic);
        var shiftD = new KeyGesture("D", GestureModifiers.Shift);
        var shiftS = new KeyGesture("S", GestureModifiers.Shift);

        Assert.Contains(CommandIds.SelectSameTexture, map.Match(shiftD, CommandScope.Face, registry));
        Assert.Contains(CommandIds.SelectGrow, map.Match(shiftS, CommandScope.Face, registry));

        // Shift+D does not leak into unrelated modes.
        Assert.DoesNotContain(CommandIds.SelectSameTexture, map.Match(shiftD, CommandScope.Object, registry));

        // The merged Face-scope texture bindings keep the shipped presets conflict-free.
        Assert.Empty(map.FindConflicts(registry));
    }

    [Fact]
    public void Texture_Ops_Are_Face_Scoped_And_Geometry_Wins_Collisions()
    {
        // Item 0h: texture/UV commands moved from a distinct Texture scope into Face scope.
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Keymap map = Keymap.FromPreset(CommandCatalog.RedClassic);

        // A texture-mapping op now fires in Face mode on the face selection.
        Assert.Contains(CommandIds.TexMapBox, map.Match(new KeyGesture("Q", GestureModifiers.Ctrl), CommandScope.Face, registry));

        // The two deliberate collisions: the geometry op keeps the gesture; the texture
        // variant lost its binding (reached from the Texture/UV tab's toolbar button).
        var ctrlE = new KeyGesture("E", GestureModifiers.Ctrl);
        Assert.Contains(CommandIds.FaceExtrude, map.Match(ctrlE, CommandScope.Face, registry));
        Assert.DoesNotContain(CommandIds.TexMapPlanar, map.Match(ctrlE, CommandScope.Face, registry));

        var shiftS = new KeyGesture("S", GestureModifiers.Shift);
        Assert.Contains(CommandIds.SelectGrow, map.Match(shiftS, CommandScope.Face, registry));
        Assert.DoesNotContain(CommandIds.TexGrow, map.Match(shiftS, CommandScope.Face, registry));

        // Shift+T (RED muscle memory) still exists as a global command (focuses the tools).
        Assert.Contains(CommandIds.ModeTexture, map.Match(new KeyGesture("T", GestureModifiers.Shift), CommandScope.Global, registry));

        // Every former Texture-scope command is registered in the Face scope now.
        foreach (string id in new[] { CommandIds.TexMapBox, CommandIds.TexMapCylinder, CommandIds.TexApply, CommandIds.TexPick, CommandIds.TexUvUnwrap, CommandIds.TexReselect })
        {
            Assert.Equal(CommandScope.Face, registry.Find(id)!.Scope);
        }
    }

    [Fact]
    public void Secondary_Scope_Command_Conflicts_When_A_Second_Command_Shares_Its_Face_Scope()
    {
        // The def-based conflict detector must see the secondary scope: binding a Face
        // command onto Shift+D now clashes with Select Same Texture's secondary Face scope.
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Keymap map = Keymap.FromPreset(CommandCatalog.RedClassic);
        map.Rebind(CommandIds.FaceBevel, new KeyGesture("D", GestureModifiers.Shift)); // FaceBevel is Face-scoped

        KeyConflict? c = map.FindConflicts(registry)
            .FirstOrDefault(k => k.Gesture.Equals(new KeyGesture("D", GestureModifiers.Shift)));
        Assert.NotNull(c);
        Assert.Contains(CommandIds.SelectSameTexture, c!.CommandIds);
        Assert.Contains(CommandIds.FaceBevel, c.CommandIds);
    }

    [Fact]
    public void Keymap_Store_Round_Trips_Preset_And_Overrides()
    {
        Keymap map = Keymap.FromPreset(CommandCatalog.Modern);
        map.Rebind(CommandIds.SelectInvert, new KeyGesture("Q", GestureModifiers.Ctrl));
        map.Rebind(CommandIds.SelectByUid, null); // explicit unbind

        string json = KeymapStore.Serialize(map);
        Keymap loaded = KeymapStore.Deserialize(json);

        Assert.Equal(CommandCatalog.Modern, loaded.PresetName);
        Assert.Equal(new KeyGesture("Q", GestureModifiers.Ctrl), loaded.Resolve(CommandIds.SelectInvert));
        Assert.Null(loaded.Resolve(CommandIds.SelectByUid));
        Assert.True(loaded.IsOverridden(CommandIds.SelectByUid));
    }

    [Theory]
    [InlineData("Ctrl+Shift+P", GestureModifiers.Ctrl | GestureModifiers.Shift, "P")]
    [InlineData("shift+b", GestureModifiers.Shift, "B")]
    [InlineData("F4", GestureModifiers.None, "F4")]
    [InlineData("Numpad2", GestureModifiers.None, "Numpad2")]
    public void Gesture_Parses_And_Round_Trips(string text, GestureModifiers mods, string key)
    {
        Assert.True(KeyGesture.TryParse(text, out KeyGesture g));
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Key);
        Assert.True(KeyGesture.TryParse(g.ToString(), out KeyGesture reparsed));
        Assert.Equal(g, reparsed);
    }
}

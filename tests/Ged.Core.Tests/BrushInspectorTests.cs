using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 4 regression coverage: the brush inspector metadata must expose the FULL
/// brush property list (Air/Solid, Portal, Detail, Emits Steam, Geoable, breakable
/// Material + No Debris, Life, lock state, read-only UID/time-index), the shared
/// InspectorField machinery must round-trip flag bits on a Brush, multi-select
/// mixed values must be detectable on two differing brushes, and edits must be
/// undo-safe (brushes section via EditBrushes; alpine section for Material).
/// </summary>
public sealed class BrushInspectorTests
{
    /// <summary>The full field list the Properties panel must expose for brushes.</summary>
    private static readonly string[] RequiredFields =
    {
        "Kind", "Is Portal", "Is Detail", "Emits Steam", "Is Geoable",
        "Material", "No Debris", "Life", "Locked", "UID", "Time Index",
    };

    [Fact]
    public void Catalog_Covers_The_Full_Brush_Field_List()
    {
        var have = BrushInspectorCatalog.Fields.Select(f => f.Label).ToHashSet();
        foreach (string field in RequiredFields)
        {
            Assert.True(have.Contains(field), $"brush inspector is missing the field '{field}'.");
        }

        // The Alpine breakable material combo carries the six stock materials.
        InspectorField material = Field("Material");
        Assert.Equal(new[] { "Glass", "Rock", "Wood", "Metal", "Cement", "Ice" }, material.Options);
    }

    [Fact]
    public void Flag_And_Life_Fields_Round_Trip_On_A_Brush()
    {
        var b = new Brush { Uid = 5, Rotation = Mat3.Identity };

        Field("Kind").Set(b, 1); // Air
        Assert.Equal((uint)BrushFlags.Air, b.Flags & (uint)BrushFlags.Air);
        Assert.Equal(1, (int)Field("Kind").Get(b)!);

        Field("Kind").Set(b, 0); // Solid
        Assert.Equal(0u, b.Flags & (uint)BrushFlags.Air);

        Field("Is Portal").Set(b, true);
        Field("Emits Steam").Set(b, true);
        Assert.Equal(true, Field("Is Portal").Get(b));
        Assert.Equal(true, Field("Emits Steam").Get(b));
        Assert.Equal((uint)(BrushFlags.Portal | BrushFlags.EmitsSteam), b.Flags);

        Field("Life").Set(b, 250);
        Assert.Equal(250, b.Life);
        Assert.Equal(250, Field("Life").Get(b));
    }

    [Fact]
    public void Normalize_Applies_Geoable_Implies_Detail()
    {
        var b = new Brush { Rotation = Mat3.Identity };
        Field("Is Geoable").Set(b, true);
        b.Flags = BrushInspectorCatalog.Normalize(b.Flags);

        Assert.Equal(true, Field("Is Geoable").Get(b));
        Assert.Equal(true, Field("Is Detail").Get(b)); // implied, matching BrushCreateParams.ToFlags

        // Clearing geoable does not clear detail (matches the create-params rule).
        Field("Is Geoable").Set(b, false);
        b.Flags = BrushInspectorCatalog.Normalize(b.Flags);
        Assert.Equal(true, Field("Is Detail").Get(b));
    }

    [Fact]
    public void Mixed_Values_Are_Detectable_On_Two_Differing_Brushes()
    {
        var air = new Brush { Uid = 1, Rotation = Mat3.Identity, Flags = (uint)BrushFlags.Air, Life = -1 };
        var solid = new Brush { Uid = 2, Rotation = Mat3.Identity, Flags = 0, Life = 100 };
        var pair = new[] { air, solid };

        // The panel's mixed-value rule: distinct field values across the selection.
        Assert.True(pair.Select(b => Field("Kind").Get(b)).Distinct().Count() > 1, "Kind should read mixed");
        Assert.True(pair.Select(b => Field("Life").Get(b)).Distinct().Count() > 1, "Life should read mixed");
        Assert.True(pair.Select(b => Field("Is Portal").Get(b)).Distinct().Count() == 1, "Portal should read uniform");

        // Collapsing the mixed value applies to both (the panel loops the selection).
        foreach (Brush b in pair)
        {
            Field("Kind").Set(b, 1);
        }

        Assert.True(pair.Select(b => Field("Kind").Get(b)).Distinct().Count() == 1);
        Assert.All(pair, b => Assert.Equal((uint)BrushFlags.Air, b.Flags & (uint)BrushFlags.Air));
    }

    [Fact]
    public void EditBrushes_Flag_Edit_Is_UndoSafe_And_Dirties_The_Brushes_Section()
    {
        EditorDocument doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, default, Mat3.Identity);
        RflSection brushHost = doc.Rfl.Sections.First(s => s.Content is BrushesSection);
        brushHost.Dirty = false; // isolate the edit's dirtying from CreateBrush's

        bool changed = false;
        ed.BrushesChanged += () => changed = true;

        InspectorField portal = Field("Is Portal");
        OpResult r = ed.EditBrushes(new[] { uid }, "Edit Is Portal", b => { portal.Set(b, true); return OpResult.Ok(); });

        Assert.True(r.Success);
        Assert.True(changed, "EditBrushes must raise BrushesChanged (drives the geometry-dirty flag)");
        Assert.Equal(true, portal.Get(ed.FindBrush(uid)!));
        Assert.True(brushHost.Dirty, "the flag edit must dirty the brushes section");

        doc.Undo.Undo();
        Assert.Equal(false, portal.Get(ed.FindBrush(uid)!));
    }

    [Fact]
    public void Breakable_Material_And_NoDebris_Persist_In_The_Alpine_Section_With_Undo()
    {
        EditorDocument doc = EmptyDoc();

        Assert.Equal(0, BrushBreakableProps.GetMaterial(doc, 42));
        Assert.False(BrushBreakableProps.GetNoDebris(doc, 42));

        BrushBreakableProps.SetMaterial(doc, 42, 3); // Metal
        Assert.Equal(3, BrushBreakableProps.GetMaterial(doc, 42));

        BrushBreakableProps.SetNoDebris(doc, 42, true);
        Assert.Equal(3, BrushBreakableProps.GetMaterial(doc, 42));
        Assert.True(BrushBreakableProps.GetNoDebris(doc, 42));

        AlpineLevelPropertiesSection alp = doc.Rfl.Sections.Select(s => s.Content)
            .OfType<AlpineLevelPropertiesSection>().Single();
        AlpineBreakableEntry entry = Assert.Single(alp.BreakableEntries);
        Assert.Equal(42, entry.BrushUid);
        Assert.Equal((byte)(0x80 | 3), entry.Material);

        // Undo unwinds the no-debris bit, then removes the entry the first edit created
        // (no phantom breakable brush left for the feature gate).
        doc.Undo.Undo();
        Assert.False(BrushBreakableProps.GetNoDebris(doc, 42));
        Assert.Equal(3, BrushBreakableProps.GetMaterial(doc, 42));

        doc.Undo.Undo();
        Assert.Empty(alp.BreakableEntries);
        Assert.Equal(0, BrushBreakableProps.GetMaterial(doc, 42));

        doc.Undo.Redo();
        Assert.Equal(3, BrushBreakableProps.GetMaterial(doc, 42));
    }

    private static InspectorField Field(string label) =>
        BrushInspectorCatalog.Fields.First(f => f.Label == label);

    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "test.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }
}

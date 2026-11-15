using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Modules;

namespace Ged.Core.Tests.Modules;

/// <summary>
/// The module fixture level, built in code (no stock geometry): an air room 20 × 6 × 12 m (x ∈ [−10, 10], y ∈ [0, 6],
/// z ∈ [−6, 6]) with a second room behind its +Z wall (0.5 m thick) joined by a doorway; four identical pilasters
/// (base + shaft + cap) at a 3 m pitch against the +Z wall plus a near-identical fifth (cap 3 cm taller); a structural
/// platform (z ∈ [−6, −1], top at y = 2) with a railing (rail + 7 posts) along its edge; a door frame around the
/// doorway; a beam under the ceiling spanning the room; the same crate pair twice (the second turned 90°); and
/// distractors (a floating lone box, a tiny box, a big structural pillar).
/// </summary>
internal static class ModuleTestLevel
{
    public const string Wall = "cem_wall01.tga";
    public const string Trim = "mtl_trim02.tga";
    public const string Crate = "mtl_crategreen01.tga";

    public static readonly double[] PilasterX = { -8, -5, -2, 1 };

    public const double VariantX = 4;

    private static readonly DateTime Fixed = new(2026, 1, 2, 3, 4, 5);

    /// <summary>Builds the level. <paramref name="shuffleDetails"/> shuffles the detail brushes' time order (structure stays first).</summary>
    public static EditorDocument Build(int? shuffleDetails = null)
    {
        EditorDocument doc = LevelTemplates.CreateNew(Fixed);
        var ed = new BrushEditor(doc);
        var structural = new List<Action>();
        var details = new List<Action>();

        // Structure (time order matters: rooms, then doorway, then the platform solid).
        structural.Add(() => Box(ed, new D3(0, 3, 0), new D3(20, 6, 12), Wall, air: true));
        structural.Add(() => Box(ed, new D3(0, 3, 9.25), new D3(20, 6, 5.5), Wall, air: true));
        structural.Add(() => Box(ed, new D3(7.5, 1.3, 6.25), new D3(2.0, 2.6, 0.9), Wall, air: true)); // doorway z ∈ [5.8, 6.7]
        structural.Add(() => Box(ed, new D3(5, 1, -3.5), new D3(10, 2, 5), Wall, air: false));          // platform x ∈ [0, 10], z ∈ [−6, −1]
        structural.Add(() => Box(ed, new D3(-6, 3, -4), new D3(4.5, 6, 4.5), Wall, air: false));        // a big structural pillar (not a candidate)

        // Pilasters against the +Z wall (z = 6).
        foreach (double x in PilasterX)
        {
            details.Add(() => Pilaster(ed, x, capHeight: 0.4));
        }

        details.Add(() => Pilaster(ed, VariantX, capHeight: 0.43));

        // Railing on the platform edge (z = −1): rail x ∈ [2, 8], y ∈ [2.9, 3.0]; posts at 2.05 + i·0.98.
        details.Add(() => Box(ed, new D3(5, 2.95, -1.1), new D3(6, 0.1, 0.1), Trim, detail: true));
        for (int i = 0; i < 7; i++)
        {
            double px = 2.05 + (i * 0.98);
            details.Add(() => Box(ed, new D3(px, 2.45, -1.1), new D3(0.1, 0.9, 0.1), Trim, detail: true));
        }

        // Door frame in the doorway: jambs x ∈ [6.5, 6.7] and [8.3, 8.5], lintel y ∈ [2.4, 2.6], z ∈ [5.9, 6.6].
        details.Add(() => Box(ed, new D3(6.6, 1.2, 6.25), new D3(0.2, 2.4, 0.7), Trim, detail: true));
        details.Add(() => Box(ed, new D3(8.4, 1.2, 6.25), new D3(0.2, 2.4, 0.7), Trim, detail: true));
        details.Add(() => Box(ed, new D3(7.5, 2.5, 6.25), new D3(2.0, 0.2, 0.7), Trim, detail: true));

        // A beam under the ceiling spanning the room along Z at x = −9.
        details.Add(() => Box(ed, new D3(-9, 5.8, 0), new D3(0.3, 0.4, 12), Trim, detail: true));

        // The crate pair twice (second turned 90°).
        details.Add(() => CratePair(ed, new D3(-3, 0, -3), 0));
        details.Add(() => CratePair(ed, new D3(-0.5, 0, 2.5), 90));

        // Distractors.
        details.Add(() => Box(ed, new D3(3, 4.5, 0), new D3(0.5, 0.5, 0.5), Trim, detail: true)); // floating lone box
        details.Add(() => Box(ed, new D3(5, 0.05, 3), new D3(0.1, 0.1, 0.1), Trim, detail: true)); // too small

        foreach (Action a in structural)
        {
            a();
        }

        IEnumerable<Action> order = details;
        if (shuffleDetails is int seed)
        {
            var rng = new Random(seed);
            order = details.OrderBy(_ => rng.Next()).ToList();
        }

        foreach (Action a in order)
        {
            a();
        }

        return doc;
    }

    /// <summary>A pilaster at x against the +Z wall: base 0.8 × 0.4 × 0.5, shaft 0.6 × 3.2 × 0.4, cap 0.8 × capHeight × 0.5.</summary>
    private static void Pilaster(BrushEditor ed, double x, double capHeight)
    {
        Box(ed, new D3(x, 0.2, 5.75), new D3(0.8, 0.4, 0.5), Wall, detail: true);
        Box(ed, new D3(x, 2.0, 5.8), new D3(0.6, 3.2, 0.4), Wall, detail: true);
        Box(ed, new D3(x, 3.6 + (capHeight / 2), 5.75), new D3(0.8, capHeight, 0.5), Wall, detail: true);
    }

    /// <summary>A 1.2 m crate with a 0.8 m crate on top, offset to its +X+Z corner, turned by yaw about <paramref name="at"/>.</summary>
    private static void CratePair(BrushEditor ed, D3 at, double yaw)
    {
        Box(ed, at + YawFrame.Rotate(new D3(0, 0.6, 0), yaw), new D3(1.2, 1.2, 1.2), Crate, detail: true, yaw: yaw);
        Box(ed, at + YawFrame.Rotate(new D3(0.2, 1.6, 0.2), yaw), new D3(0.8, 0.8, 0.8), Crate, detail: true, yaw: yaw);
    }

    public static int Box(BrushEditor ed, D3 centre, D3 size, string texture, bool air = false, bool detail = false, double yaw = 0)
    {
        var p = new BrushCreateParams
        {
            Shape = BrushShape.Box,
            Width = (float)size.X,
            Height = (float)size.Y,
            Depth = (float)size.Z,
            Texture = texture,
            Air = air,
            Detail = detail,
        };
        return ed.CreateBrush(p, centre.ToVec3(), yaw == 0 ? Mat3.Identity : YawFrame.Matrix(yaw));
    }

    public static List<Brush> Brushes(EditorDocument doc)
    {
        doc.Rfl.ParseAllKnownSections();
        return doc.Rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().FirstOrDefault()?.Brushes ?? new List<Brush>();
    }

    public static HarvestLevelContext Context(string level = "fixture.rfl") => new()
    {
        Level = level,
        IsStockTexture = _ => true,
        TextureResolves = _ => true,
    };

    /// <summary>Harvests the fixture and builds the library in memory.</summary>
    public static (LevelHarvestResult Level, List<BuiltModule> Modules) Harvest(EditorDocument doc)
    {
        LevelHarvestResult r = ModuleHarvester.HarvestLevel(doc.Rfl, Context());
        List<BuiltModule> built = ModuleLibraryBuilder.Build(r.Modules.Select(m => ("stock", m)).ToList(), new[] { "stock" });
        return (r, built);
    }

    /// <summary>A unique temp folder (deleted by the caller).</summary>
    public static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "glacier-modules-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

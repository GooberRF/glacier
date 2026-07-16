using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Linting;

/// <summary>The object category a <see cref="BudgetLine"/> tracks.</summary>
public enum BudgetKind
{
    /// <summary>Game-object pool: entities + items + clutter + mesh objects + bags.</summary>
    Objects,

    Lights,

    ParticleEmitters,

    Decals,

    MpRespawnPoints,

    AmbientSounds,

    /// <summary>Multiplayer switch-box clutter (class name contains "switch").</summary>
    SwitchBoxes,
}

/// <summary>
/// One budget row: how many of a category the level has, and the stock RED cap vs
/// the Alpine-raised cap. <see cref="Cap"/>/<see cref="Fraction"/> resolve against
/// the active save target so the linter and the statistics dashboard share the
/// same target-aware colouring logic.
/// </summary>
public sealed record BudgetLine(BudgetKind Kind, string Name, int Count, int StockCap, int AlpineCap)
{
    /// <summary>The applicable cap for the given save target.</summary>
    public int Cap(SaveTarget target) => target == SaveTarget.StockRf ? StockCap : AlpineCap;

    /// <summary>Fraction of the target cap consumed (0..1+; 0 when the cap is 0).</summary>
    public double Fraction(SaveTarget target)
    {
        int cap = Cap(target);
        return cap > 0 ? (double)Count / cap : 0d;
    }

    /// <summary>True when the count exceeds the stock RED cap (crash/corruption territory on stock).</summary>
    public bool OverStock => Count > StockCap;

    /// <summary>True when the count exceeds the target cap.</summary>
    public bool Over(SaveTarget target) => Count > Cap(target);

    /// <summary>Severity for this line under the given target: error over the target cap, warn at 90%.</summary>
    public LintSeverity Severity(SaveTarget target)
    {
        if (Over(target))
        {
            return LintSeverity.Error;
        }

        return Fraction(target) >= 0.9 ? LintSeverity.Warning : LintSeverity.Info;
    }
}

/// <summary>
/// Computes the target-aware object-count budgets. Caps mirror the stock RED
/// engine limits (<c>docs/research/red-stock-inventory.md §12</c>) and Alpine's
/// raised caps: objects 1024/65536, lights 1100/8192, particle emitters 128,
/// decals 96/384, MP respawns 32/2048, ambient sounds 25, switch boxes 32.
/// </summary>
public static class LevelBudget
{
    /// <summary>Builds every budget line from the level's parsed sections.</summary>
    public static IReadOnlyList<BudgetLine> Compute(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        rfl.ParseAllKnownSections();

        int entities = 0, items = 0, clutter = 0, mesh = 0, bags = 0;
        int lights = 0, emitters = 0, decals = 0, respawns = 0, ambient = 0, switches = 0;

        foreach (RflSection section in rfl.Sections)
        {
            switch (section.Content)
            {
                case EntitiesSection s: entities += s.Entities.Count; break;
                case ItemsSection s: items += s.Items.Count; break;
                case CluttersSection s:
                    clutter += s.Clutters.Count;
                    switches += s.Clutters.Count(c =>
                        c.Header.ClassName.Contains("switch", StringComparison.OrdinalIgnoreCase));
                    break;
                case AlpineMeshObjectsSection s: mesh += s.Meshes.Count; break;
                case AlpineBagObjectsSection s: bags += s.Bags.Count; break;

                // Editor-only lights do not consume the runtime light pool.
                case LightsSection s when s.Type == SectionType.Lights: lights += s.Lights.Count; break;
                case ParticleEmittersSection s: emitters += s.Emitters.Count; break;
                case DecalsSection s: decals += s.Decals.Count; break;
                case MpRespawnPointsSection s: respawns += s.Points.Count; break;
                case AmbientSoundsSection s: ambient += s.Sounds.Count; break;
            }
        }

        int objects = entities + items + clutter + mesh + bags;

        return new[]
        {
            new BudgetLine(BudgetKind.Objects, "Objects", objects, 1024, 65536),
            new BudgetLine(BudgetKind.Lights, "Lights", lights, 1100, 8192),
            new BudgetLine(BudgetKind.ParticleEmitters, "Particle Emitters", emitters, 128, 128),
            new BudgetLine(BudgetKind.Decals, "Decals", decals, 96, 384),
            new BudgetLine(BudgetKind.MpRespawnPoints, "MP Respawn Points", respawns, 32, 2048),
            new BudgetLine(BudgetKind.AmbientSounds, "Ambient Sounds", ambient, 25, 25),
            new BudgetLine(BudgetKind.SwitchBoxes, "Switch Boxes", switches, 32, 32),
        };
    }
}

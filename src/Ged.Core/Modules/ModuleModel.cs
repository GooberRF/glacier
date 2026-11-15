using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Modules;

/// <summary>The module vocabulary: categories, roles, attachment kinds, style families (one place).</summary>
public static class ModuleVocabulary
{
    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "pilaster", "buttress", "wall_panel", "recess", "base_trim", "top_trim", "band", "door_frame", "window_frame", "beam",
        "truss", "ceiling_coffer", "column", "catwalk", "platform", "railing", "stairs", "ladder", "pipe", "duct", "machinery",
        "console", "crate_stack", "light_housing", "rock", "ledge", "support", "grate", "misc",
    };

    public static readonly IReadOnlyList<string> Roles = new[] { "cover", "route", "landmark", "theme_detail", "fixture" };

    public static readonly IReadOnlyList<string> Attachments = new[] { "wall", "corner", "floor", "ceiling", "column", "span", "edge", "opening", "free" };

    public static readonly IReadOnlyList<string> Styles = new[] { "industrial", "bunker", "admin", "mine", "cave", "canyon", "mars_base", "lab", "tech", "generic" };

    /// <summary>What the local origin means for each attachment kind (stored in the index as <c>anchor</c>).</summary>
    public static string AnchorText(string attach) => attach switch
    {
        "wall" => "wall plane at z=0, module extends +Z into the room; x centred; y=0 at the floor (or the module bottom, see mount_height)",
        "corner" => "walls at z=0 and x=0, module extends +X,+Z; y=0 at the floor (or the module bottom, see mount_height)",
        "floor" => "bottom-centre: y=0 at the floor plane, x and z centred",
        "ceiling" => "y=0 at the ceiling plane, module extends -Y; x and z centred",
        "column" => "y=0 at the floor, x and z centred; spans floor to ceiling",
        "span" => "x along the span (centred), z centred; y=0 at the ceiling when touching it (module extends -Y), else at the module bottom",
        "edge" => "x along the edge (centred), z=0 on the edge line, +Z toward the walkable side; y=0 at the floor",
        "opening" => "x centred on the opening, y=0 at the sill/floor, z=0 at the mid-plane of the wall",
        _ => "bottom-centre of the module (no solid contact found)",
    };

    /// <summary>The axis letter of an axis index.</summary>
    public static string AxisName(int axis) => axis switch { 0 => "x", 1 => "y", _ => "z" };

    /// <summary>The axis index of an axis letter, or -1.</summary>
    public static int AxisIndex(string? axis) => axis?.ToLowerInvariant() switch { "x" => 0, "y" => 1, "z" => 2, _ => -1 };
}

/// <summary>A 3-slice stretch zone on one local axis: vertices at or below <see cref="A"/> stay, those at or above <see cref="B"/> shift.</summary>
public sealed record ModuleSlice(double A, double B)
{
    public double Length => B - A;
}

/// <summary>
/// A tile family: ≥ 3 congruent units repeating at a constant pitch along one local axis (railing posts, rungs,
/// ribs). Unit 0 is the unit nearest the −axis end; on a stretch the replicas are dropped and unit 0 re-instanced.
/// </summary>
public sealed class ModuleTileFamily
{
    /// <summary>"x", "y" or "z".</summary>
    public string Axis { get; init; } = "x";

    public double Pitch { get; init; }

    /// <summary>Brush indices (module order) of unit 0.</summary>
    public IReadOnlyList<int> First { get; init; } = Array.Empty<int>();

    /// <summary>Brush indices of every other unit (dropped and re-instanced on a stretch).</summary>
    public IReadOnlyList<int> Replicas { get; init; } = Array.Empty<int>();

    public int Count { get; init; }

    /// <summary>Unit 0's centre on the axis.</summary>
    public double FirstCenter { get; init; }

    /// <summary>Distance from the stretch zone start to unit 0's centre.</summary>
    public double MarginStart { get; init; }

    /// <summary>Distance from the last unit's centre to the stretch zone end.</summary>
    public double MarginEnd { get; init; }

    public int AxisIndex => ModuleVocabulary.AxisIndex(Axis);
}

/// <summary>One texture a module uses.</summary>
/// <param name="Name">The texture file name as the brushes reference it.</param>
/// <param name="Family">Its name family (prefix, e.g. <c>mtl</c>, <c>rck</c>).</param>
/// <param name="Stock">True when a stock packfile holds it (every player has it).</param>
/// <param name="Source">The packfile (relative to the manifest root) that supplies a custom texture, else null.</param>
public sealed record ModuleTexture(string Name, string Family, bool Stock, string? Source);

/// <summary>One place a module was found.</summary>
/// <param name="Level">The level file (relative to the manifest root; a packfile level is <c>pack.vpp|level.rfl</c>).</param>
/// <param name="Packfile">The packfile holding the level, or null for a loose level.</param>
/// <param name="Position">World position of the module's local origin.</param>
/// <param name="Yaw">World yaw of the module's local frame (degrees).</param>
/// <param name="BrushUids">The source brush UIDs (module order).</param>
public sealed record ModuleProvenance(string Level, string? Packfile, D3 Position, double Yaw, IReadOnlyList<int> BrushUids);

/// <summary>The clear opening a frame surrounds.</summary>
public sealed record ModuleOpening(double Width, double Height, double WallThickness);

/// <summary>
/// One harvested module: its classification, local-frame size and bounds, stretch slices, tile families, textures,
/// mounting measurements, score and provenance. Its brushes live in <see cref="File"/> (an .rfg beside the index).
/// </summary>
public sealed class ModuleInfo
{
    public string Id { get; init; } = string.Empty;

    /// <summary>The source group (e.g. <c>stock</c>).</summary>
    public string Group { get; init; } = string.Empty;

    private string _category = "misc";
    private string _role = "theme_detail";
    private IReadOnlyList<string> _styles = Array.Empty<string>();

    public string Category { get => _category; init => _category = value; }

    public string Role { get => _role; init => _role = value; }

    public string Attach { get; init; } = "free";

    /// <summary>Style families, primary first.</summary>
    public IReadOnlyList<string> Styles { get => _styles; init => _styles = value; }

    /// <summary>The mapper's verdict from <c>curation.json</c>: "keep", "reject", or null (undecided).</summary>
    public string? Verdict { get; private set; }

    /// <summary>The mapper's note from <c>curation.json</c>, or null.</summary>
    public string? CurationNote { get; private set; }

    /// <summary>True when <c>curation.json</c> changed the category, role or styles.</summary>
    public bool Relabelled { get; private set; }

    /// <summary>The harvested (category, role, styles) before a relabel, or null when not relabelled.</summary>
    public (string Category, string Role, IReadOnlyList<string> Styles)? Harvested { get; private set; }

    /// <summary>True when the mapper rejected the module (it leaves every selection unless asked for).</summary>
    public bool Rejected => Verdict == "reject";

    /// <summary>True when the mapper kept the module.</summary>
    public bool Kept => Verdict == "keep";

    /// <summary>The score selections rank by: <see cref="Score"/>, plus <see cref="ModuleCuration.KeepBonus"/> when kept.</summary>
    public double SelectionScore => Kept ? Score + ModuleCuration.KeepBonus : Score;

    public D3 Size { get; init; }

    public D3 BoundsMin { get; init; }

    public D3 BoundsMax { get; init; }

    public string Anchor => ModuleVocabulary.AnchorText(Attach);

    public ModuleSlice? StretchX { get; init; }

    public ModuleSlice? StretchY { get; init; }

    public ModuleSlice? StretchZ { get; init; }

    public IReadOnlyList<ModuleTileFamily> Tiles { get; init; } = Array.Empty<ModuleTileFamily>();

    public IReadOnlyList<ModuleTexture> Textures { get; init; } = Array.Empty<ModuleTexture>();

    public bool CustomTextures => Textures.Any(t => !t.Stock);

    public int Brushes { get; init; }

    public int Faces { get; init; }

    /// <summary>True when the module has air members (it carves a recess).</summary>
    public bool HasAir { get; init; }

    public double? RecessDepth { get; init; }

    /// <summary>Gap from the floor below to the module bottom (when not standing on the floor).</summary>
    public double? MountHeight { get; init; }

    /// <summary>Gap from the module top to the ceiling above (null when none within 40 m).</summary>
    public double? CeilingGap { get; init; }

    public ModuleOpening? Opening { get; init; }

    public double Score { get; init; }

    public int Occurrences { get; init; }

    /// <summary>Median spacing between recurrences along a wall / edge (when ≥ 3 collinear recurrences exist).</summary>
    public double? SuggestedPitch { get; init; }

    /// <summary>Where it was found, representative first (capped; see <see cref="Occurrences"/> for the total).</summary>
    public IReadOnlyList<ModuleProvenance> Provenance { get; init; } = Array.Empty<ModuleProvenance>();

    /// <summary>The module's .rfg, relative to its group folder ('/'-separated).</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>The full path of the .rfg (set when loaded from a library; not serialized).</summary>
    public string? RfgPath { get; init; }

    /// <summary>The stretch slice of an axis (0 = X, 1 = Y, 2 = Z), or null.</summary>
    public ModuleSlice? Stretch(int axis) => axis switch { 0 => StretchX, 1 => StretchY, _ => StretchZ };

    /// <summary>The stretchable axes as letters ("x", "y", "z").</summary>
    public IReadOnlyList<string> StretchAxes =>
        Enumerable.Range(0, 3).Where(a => Stretch(a) is not null).Select(ModuleVocabulary.AxisName).ToArray();

    public string PrimaryStyle => Styles.Count > 0 ? Styles[0] : "generic";

    /// <summary>A copy with the mapper's curation applied (verdict, note, relabels); the harvested values are kept in <see cref="Harvested"/>.</summary>
    public ModuleInfo WithCuration(ModuleCurationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var c = (ModuleInfo)MemberwiseClone();
        c.Verdict = entry.Verdict;
        c.CurationNote = entry.Note;
        bool relabel = (entry.Category is not null && entry.Category != _category)
            || (entry.Role is not null && entry.Role != _role)
            || (entry.Styles is not null && !entry.Styles.SequenceEqual(_styles, StringComparer.Ordinal));
        if (relabel)
        {
            c.Harvested = (_category, _role, _styles);
            c._category = entry.Category ?? _category;
            c._role = entry.Role ?? _role;
            c._styles = entry.Styles?.ToArray() ?? _styles;
            c.Relabelled = true;
        }

        return c;
    }

    public override string ToString() => FormattableString.Invariant($"{Id} ({Category}, {Attach}, {Size.X:0.##}x{Size.Y:0.##}x{Size.Z:0.##} m)");
}

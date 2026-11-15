using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Automation.Reference;

/// <summary>One known Red Faction dimension an agent can count in a screenshot.</summary>
/// <param name="Unit">The name used in conversions (<c>door_height</c>, <c>player</c>, ...).</param>
/// <param name="Metres">The typical value.</param>
/// <param name="Range">The usual range [low, high] (the 25th-75th percentile where measured).</param>
/// <param name="What">What it is.</param>
/// <param name="Source">Where the number comes from.</param>
public sealed record ScaleCue(string Unit, double Metres, double[] Range, string What, string Source);

/// <summary>
/// Known dimensions for turning "the wall is about three doors tall" into metres. The door, doorway, corridor, headroom
/// and stair numbers were measured on the stock levels (dm01-dm20, ctf01-ctf07 and the 68 single-player levels) with
/// glacier-cli: door panels are the single-brush movers flagged as doors (948 panels in 61 levels), doorways and
/// corridors are the fingerprint measurements (<c>glacier-cli analyze</c>: the median of each level's median). The
/// player numbers come from the engine tables and Alpine Faction's movement code.
/// </summary>
public static class ReferenceScale
{
    /// <summary>The cues, most useful first.</summary>
    public static IReadOnlyList<ScaleCue> Cues { get; } = new List<ScaleCue>
    {
        new("door_height", 2.5, new[] { 2.5, 3.0 },
            "Height of a door panel (a sliding mover door); 2.5 m is by far the most common (438 of 948 panels), multiplayer doors are taller (median 3.0 m).",
            "measured: stock levels, single-brush door movers (median 2.56, p25 2.5, p75 3.0; dm/ctf median 3.0)"),
        new("door_width", 2.0, new[] { 1.5, 3.0 },
            "Width of one door panel; a split door is two panels (1-1.5 m halves are common), multiplayer panels median 3.0 m.",
            "measured: stock levels, single-brush door movers (median 2.0, p25 1.5, p75 3.0)"),
        new("doorway_width", 2.25, new[] { 1.9, 3.2 },
            "Width of a doorway opening in the walls (single-player median 2.1, deathmatch 3.1, CTF 3.7).",
            "measured: glacier-cli analyze opening.width on 86 stock levels (median of level medians)"),
        new("doorway_height", 4.1, new[] { 3.5, 6.0 },
            "Height of a doorway opening, arches included (taller than the door panels that close the smaller ones).",
            "measured: glacier-cli analyze opening.height on 86 stock levels"),
        new("player", 1.8, new[] { 1.7, 1.9 },
            "A standing player figure, head to foot (use for people, guards, miners in a picture).",
            "approximate: the player model's height; not measured from the mesh"),
        new("eye", ReferenceCamera.StandingEye, new[] { 1.6, 1.7 },
            "Eye height of a standing player above the floor: where a first-person screenshot's camera is.",
            "convention used by Glacier's eye-height renders (--eye-height eye)"),
        new("player_clearance", 2.0, new[] { 2.0, 2.0 },
            "Headroom a standing player needs (1.375 m crouched); a gap lower than this cannot be walked.",
            "Alpine Faction game_patch/misc/waypoints.h:43-45 (as used by Glacier's spatial queries)"),
        new("jump", 1.33, new[] { 1.33, 1.33 },
            "The highest ledge a player can jump onto.",
            "Red Faction game.tbl: $Max Entity Jump Height 1.33"),
        new("corridor_width", 4.75, new[] { 4.0, 5.65 },
            "Clear width of a corridor.",
            "measured: glacier-cli analyze corridor.width on 94 stock levels"),
        new("corridor_height", 4.5, new[] { 4.0, 5.65 },
            "Clear height of a corridor.",
            "measured: glacier-cli analyze corridor.height on 94 stock levels"),
        new("room_height", 5.0, new[] { 4.0, 6.5 },
            "Typical floor-to-ceiling height of a playable space (deathmatch median 5.0, CTF 4.0, single-player 6.5).",
            "measured: glacier-cli analyze space.headroom on 94 stock levels (median of level medians)"),
        new("stair_riser", 0.25, new[] { 0.25, 0.25 },
            "Height of one stair step.",
            "measured: glacier-cli analyze stair.riser on the 30 stock levels with stairs"),
        new("stair_tread", 0.75, new[] { 0.5, 0.88 },
            "Depth of one stair step.",
            "measured: glacier-cli analyze stair.tread on the 30 stock levels with stairs"),
    };

    /// <summary>The cue for a unit name (case-insensitive; '-' and ' ' read as '_'), or a <c>usage</c> error.</summary>
    public static ScaleCue Find(string unit)
    {
        string key = unit.Trim().Replace('-', '_').Replace(' ', '_');
        return Cues.FirstOrDefault(c => string.Equals(c.Unit, key, StringComparison.OrdinalIgnoreCase))
            ?? Cues.FirstOrDefault(c => string.Equals(c.Unit, key.TrimEnd('s'), StringComparison.OrdinalIgnoreCase))
            ?? throw new AutomationException(AutomationErrorCodes.Usage,
                $"Unknown unit '{unit}' (use {string.Join(", ", Cues.Select(c => c.Unit))}).");
    }
}

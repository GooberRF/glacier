using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Modules;

/// <summary>The shape / context features a module is classified from (all in its local frame).</summary>
internal sealed class ModuleFeatures
{
    public required string Attach { get; init; }

    public D3 Size { get; init; }

    public int Members { get; init; }

    public int SolidMembers { get; init; }

    public int AirMembers { get; init; }

    public int Faces { get; init; }

    public bool FloorTouch { get; init; }

    public bool CeilingTouch { get; init; }

    public double? MountHeight { get; init; }

    public double? CeilingGap { get; init; }

    public ModuleOpening? Opening { get; init; }

    /// <summary>True when an opening frame sits in a wall (else it is a free-standing portal).</summary>
    public bool OpeningInWall { get; init; }

    /// <summary>Area fraction of faces whose normal is more than 5° off every axis.</summary>
    public double OffAxis { get; init; }

    /// <summary>Upward-facing sloped face area (normal.y in 0.2..0.95) over the footprint.</summary>
    public double SlopedTop { get; init; }

    /// <summary>Horizontal upward face area at the top (within 0.1 m of the top) over the footprint.</summary>
    public double FlatTop { get; init; }

    /// <summary>Volume fraction of members that are round prisms (≥ 8 side faces, roundish cross-section).</summary>
    public double Roundness { get; init; }

    /// <summary>Air volume over solid volume.</summary>
    public double AirRatio { get; init; }

    /// <summary>Treads stepping in Y along one horizontal axis.</summary>
    public bool StairSteps { get; init; }

    /// <summary>Distinct member shapes (by size class).</summary>
    public int DistinctShapes { get; init; }

    public IReadOnlyList<ModuleTileFamily> Tiles { get; init; } = Array.Empty<ModuleTileFamily>();

    /// <summary>Area shares of texture-name tokens (pipe, rock, crate, light, ladder, grate, tech, duct, stair, support).</summary>
    public IReadOnlyDictionary<string, double> Tokens { get; init; } = new Dictionary<string, double>();

    public double Token(string name) => Tokens.TryGetValue(name, out double v) ? v : 0;

    public double Longest => Math.Max(Size.X, Math.Max(Size.Y, Size.Z));

    public double HorizontalLong => Math.Max(Size.X, Size.Z);

    public double HorizontalShort => Math.Min(Size.X, Size.Z);
}

/// <summary>
/// Rule-based module classification (general rules on shape, attachment and texture tokens; no level knowledge):
/// category with a confidence, and the gameplay role.
/// </summary>
internal static class ModuleClassifier
{
    /// <summary>Texture-name tokens: (token, substrings).</summary>
    public static readonly IReadOnlyList<(string Token, string[] Fragments)> TokenRules = new[]
    {
        ("pipe", new[] { "pipe", "tube", "conduit" }),
        ("rock", new[] { "rck_", "rock", "boulder", "cliff", "canyon" }),
        ("crate", new[] { "crate", "cargo", "box_", "barrel" }),
        ("light", new[] { "light", "lamp", "glow", "fixture" }),
        ("ladder", new[] { "ladder", "rung" }),
        ("grate", new[] { "grate", "grating", "grill", "mesh", "walkway", "catwalk" }),
        ("tech", new[] { "tech", "contrl", "control", "computer", "comp_", "console", "screen", "monitor", "keypad", "switch", "panel" }),
        ("duct", new[] { "duct", "vent" }),
        ("stair", new[] { "stair", "step" }),
        ("support", new[] { "girder", "support", "truss", "beam", "strut", "brace", "crossmember" }),
        ("trim", new[] { "trim", "border", "edge" }),
    };

    /// <summary>The tokens of one texture name.</summary>
    public static IEnumerable<string> TokensOf(string texture)
    {
        string n = System.IO.Path.GetFileNameWithoutExtension(texture ?? string.Empty).ToLowerInvariant();
        foreach ((string token, string[] frags) in TokenRules)
        {
            if (frags.Any(f => n.Contains(f, StringComparison.Ordinal)))
            {
                yield return token;
            }
        }
    }

    /// <summary>Category + confidence (0..1).</summary>
    public static (string Category, double Confidence) Classify(ModuleFeatures f)
    {
        double sx = f.Size.X, sy = f.Size.Y, sz = f.Size.Z;
        double longH = f.HorizontalLong, shortH = f.HorizontalShort;
        double thin = Math.Min(sx, Math.Min(sy, sz));
        bool flat = thin < 0.02;
        bool onFloor = f.FloorTouch || f.MountHeight is <= 0.3;

        // Route pieces first: their shape is unmistakable.
        if (f.StairSteps && !flat)
        {
            return ("stairs", 0.85);
        }

        if (f.Token("ladder") >= 0.3 && sy >= 1.0 && shortH <= 1.0)
        {
            return ("ladder", 0.8);
        }

        if (f.Tiles.Any(t => t.Axis == "y" && t.Count >= 4) && sx <= 1.5 && sz <= 0.8 && sy >= 1.5)
        {
            return ("ladder", 0.7);
        }

        // Railings: low, long, thin, standing on an edge, a floor or free (a fence texture on a plane counts).
        if (sy is >= 0.4 and <= 1.4 && longH >= 1.5 && longH >= 3 * shortH && shortH <= 0.5
            && f.Attach is "edge" or "floor" or "free")
        {
            double conf = f.Attach == "edge" ? 0.8 : 0.6;
            if (f.Tiles.Any(t => t.Axis == "x"))
            {
                conf += 0.1;
            }

            return ("railing", conf);
        }

        // Flat (single-plane) pieces: grates, wall panels and signs; nothing structural.
        if (flat)
        {
            if (f.Token("grate") >= 0.3 || (sy < 0.02 && longH >= 0.5))
            {
                return ("grate", f.Token("grate") >= 0.3 ? 0.65 : 0.4);
            }

            return f.Attach is "wall" or "corner" ? ("wall_panel", 0.45) : ("misc", 0.25);
        }

        // Frames around an opening in a wall; a free-standing portal (a tunnel support) is a support.
        if (f.Attach == "opening" && f.Opening is { } o)
        {
            // A frame around a vast opening (wider than 6 m or taller than 5 m) is a portal or support, not a door or window.
            if (!f.OpeningInWall || o.Width > 6 || o.Height > 5)
            {
                return ("support", 0.55);
            }

            bool door = f.FloorTouch || f.MountHeight is < 0.3;
            return (door ? "door_frame" : "window_frame", o.Height >= 1.8 && o.Width <= 6 ? 0.85 : 0.65);
        }

        // Pipes and ducts: long round (or pipe-textured) extrusions.
        double crossMax = SecondLargest(sx, sy, sz);
        bool longThin = f.Longest >= 2.5 * crossMax && f.Longest >= 1.0;
        if (longThin && (f.Roundness >= 0.5 || f.Token("pipe") >= 0.4) && crossMax <= 2.0)
        {
            return ("pipe", f.Roundness >= 0.5 && f.Token("pipe") >= 0.4 ? 0.85 : 0.7);
        }

        if (longThin && f.Token("duct") >= 0.4 && crossMax <= 2.5)
        {
            return ("duct", 0.7);
        }

        // Rocks: rock textures, or irregular geometry (most face area off the axes).
        if (f.Token("rock") >= 0.5 && (f.OffAxis >= 0.3 || f.Attach is "floor" or "free" or "ceiling" or "column"))
        {
            return ("rock", f.OffAxis >= 0.4 ? 0.8 : 0.6);
        }

        if (f.OffAxis >= 0.6 && f.Faces >= 10 && f.Roundness < 0.3 && f.Token("tech") < 0.2)
        {
            return ("rock", 0.55);
        }

        // Decks with air below and headroom above: catwalks, platforms and grates.
        bool deck = sy <= 0.6 && f.FlatTop >= 0.5 && shortH >= 0.6 && !f.CeilingTouch && f.CeilingGap is null or >= 1.5;
        bool airBelow = !f.FloorTouch && (f.MountHeight is null or >= 0.6);
        if (deck && airBelow && f.Attach != "ceiling")
        {
            if (f.Token("grate") >= 0.4 && sy <= 0.3)
            {
                return longH >= 2.5 * shortH ? ("catwalk", 0.75) : ("grate", 0.7);
            }

            return longH >= 2.5 * shortH || (longH >= 6 && shortH <= 3) ? ("catwalk", 0.7) : ("platform", 0.65);
        }

        if (f.Token("grate") >= 0.5 && thin <= 0.3)
        {
            return ("grate", 0.6);
        }

        switch (f.Attach)
        {
            case "column":
                if (longH <= 3 && sy >= 2 * longH)
                {
                    return ("column", 0.8);
                }

                break;

            case "wall":
            case "corner":
                return ClassifyWall(f, onFloor);

            case "span":
                if (sx >= 2.5 * Math.Max(sy, sz) && Math.Max(sy, sz) >= 0.2)
                {
                    return IsTruss(f) ? ("truss", 0.75) : ("beam", 0.8);
                }

                if (f.Token("light") >= 0.3)
                {
                    return ("light_housing", 0.6);
                }

                return ("support", 0.4);

            case "ceiling":
                if (sx >= 3 * Math.Max(sy, sz) || sz >= 3 * Math.Max(sx, sy))
                {
                    return IsTruss(f) ? ("truss", 0.65) : ("beam", 0.65);
                }

                if (f.Token("light") >= 0.25 && f.Longest <= 4)
                {
                    return ("light_housing", 0.7);
                }

                if ((f.AirMembers > 0 || f.Members >= 2) && sy <= 1.2 && sx >= 1 && sz >= 1)
                {
                    return ("ceiling_coffer", 0.5);
                }

                if (f.OffAxis >= 0.3 && f.Longest <= 4)
                {
                    return ("support", 0.45);
                }

                break;
        }

        // Floor-standing and free pieces.
        if (f.Attach is "floor" or "edge" or "free" or "column")
        {
            if (f.Attach == "floor" && sy >= 2.5 && longH <= 3 && sy >= 3 * longH)
            {
                return ("column", 0.65);
            }

            if (f.Token("crate") >= 0.4)
            {
                return ("crate_stack", 0.8);
            }

            if (f.Attach is "floor" or "edge" && sy is >= 0.7 and <= 1.8 && longH <= 3 && shortH >= 0.3
                && (f.Token("tech") >= 0.25 || f.SlopedTop >= 0.1))
            {
                return ("console", f.Token("tech") >= 0.25 && f.SlopedTop >= 0.1 ? 0.8 : 0.6);
            }

            if (f.Token("light") >= 0.3 && f.Longest <= 3)
            {
                return ("light_housing", 0.6);
            }

            if (f.Members >= 4 && thin >= 0.4 && thin >= 0.2 * f.Longest && f.DistinctShapes >= 2)
            {
                return ("machinery", f.Token("tech") >= 0.2 || f.Roundness >= 0.2 ? 0.65 : 0.5);
            }

            if (f.OffAxis >= 0.3 && f.Members <= 6 && f.Longest <= 4 && f.Token("support") >= 0.3)
            {
                return ("support", 0.5);
            }
        }

        return ("misc", 0.3);
    }

    private static (string, double) ClassifyWall(ModuleFeatures f, bool onFloor)
    {
        double sx = f.Size.X, sy = f.Size.Y, sz = f.Size.Z;
        if (f.AirMembers > 0 && f.AirRatio >= 1.0)
        {
            return ("recess", 0.7);
        }

        if (onFloor && sy >= 1.5 && sy >= 2 * sx && sx <= 2.0 && sz is >= 0.08 and <= 1.5)
        {
            return ("pilaster", sz <= 0.8 ? 0.8 : 0.65);
        }

        if (onFloor && sz > sx && sz >= 0.6 && sz <= 1.2 * sy && sy >= 1.0 && (f.SlopedTop > 0.05 || f.OffAxis >= 0.2))
        {
            return ("buttress", 0.6);
        }

        if (sx >= 1.0 && sx >= 4 * Math.Max(sy, sz) && sy <= 1.0 && sz is >= 0.03 and <= 0.6)
        {
            if (f.FloorTouch || f.MountHeight is < 0.05)
            {
                return ("base_trim", 0.8);
            }

            if (f.CeilingTouch || f.CeilingGap is <= 0.1)
            {
                return ("top_trim", 0.8);
            }

            return ("band", 0.7);
        }

        if (f.Token("light") >= 0.25 && f.Longest <= 3)
        {
            return ("light_housing", 0.7);
        }

        if (sz >= 0.3 && sz <= 2.0 && sy <= 0.6 && sx >= 2 * sz && f.FlatTop >= 0.5 && !onFloor)
        {
            return ("ledge", 0.6);
        }

        if (f.Token("tech") >= 0.3 && f.Longest <= 2.5 && sx >= 0.3 && sy >= 0.3 && sz >= 0.1)
        {
            return ("console", 0.5);
        }

        if (sz <= 0.4 && sx >= 0.5 && sy >= 0.5)
        {
            return ("wall_panel", 0.6);
        }

        if (f.Token("pipe") >= 0.4)
        {
            return ("pipe", 0.5);
        }

        if (f.OffAxis >= 0.3 && f.Members <= 6 && f.Longest <= 4)
        {
            return ("support", 0.5);
        }

        if (f.AirMembers > 0)
        {
            return ("recess", 0.45);
        }

        return ("misc", 0.3);
    }

    private static bool IsTruss(ModuleFeatures f) => f.Members >= 4 && (f.OffAxis >= 0.15 || f.Tiles.Count > 0);

    private static double SecondLargest(double a, double b, double c)
    {
        double[] v = { a, b, c };
        Array.Sort(v);
        return v[1];
    }

    /// <summary>The gameplay role of a classified module.</summary>
    public static string Role(string category, ModuleFeatures f)
    {
        if (category is "stairs" or "ladder" or "catwalk" or "platform" or "railing")
        {
            return "route";
        }

        double vol = f.Size.X * f.Size.Y * f.Size.Z;
        bool standalone = f.Attach is "floor" or "free" or "column" or "edge";
        if (standalone && (f.Longest >= 6 || vol >= 60) && category is "machinery" or "rock" or "misc" or "column" or "crate_stack" or "console")
        {
            return "landmark";
        }

        if (f.Attach is "floor" or "edge" && f.Size.Y is >= 1.0 and <= 2.6 && f.HorizontalLong >= 1.0
            && category is "crate_stack" or "machinery" or "rock" or "misc" or "console")
        {
            return "cover";
        }

        if (category is "console" or "light_housing")
        {
            return "fixture";
        }

        if (f.Attach is "wall" or "corner" && category == "misc" && f.Longest <= 1.5)
        {
            return "fixture";
        }

        return "theme_detail";
    }
}

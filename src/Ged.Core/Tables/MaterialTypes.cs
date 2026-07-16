using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ged.Core.Tables;

/// <summary>
/// Impact-material and damage-type name→index maps shared by the clutter/entity catalogs
/// and the object→mesh converter. Mirrors Alpine's <c>tbl_parse_material</c> /
/// <c>tbl_parse_damage_type</c> (editor_patch/tbl.cpp:33-56) exactly so a converted mesh
/// inherits the same material index and damage-factor slots the game assigns.
/// </summary>
public static class MaterialTypes
{
    private static readonly string[] Materials =
        { "Default", "Rock", "Metal", "Flesh", "Water", "Lava", "Solid", "Sand", "Ice", "Glass" };

    // Alpine's damage-type order (tbl.cpp:47-51): the .tbl "$Damage Type Factor:" name maps to a
    // factor slot. Only these nine names resolve; unnamed slots (9, 10) keep their default factor.
    private static readonly (string Name, int Index)[] DamageTypes =
    {
        ("bash", 0), ("bullet", 1), ("armor piercing bullet", 2),
        ("explosive", 3), ("fire", 4), ("energy", 5),
        ("electrical", 6), ("acid", 7), ("scalding", 8),
    };

    /// <summary>Impact-material index (0 Default … 9 Glass); 0 for an unknown/empty name.</summary>
    public static int ParseMaterial(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        for (int i = 0; i < Materials.Length; i++)
        {
            if (string.Equals(name, Materials[i], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Damage-type factor slot (0..8) for a name, or -1 for an unknown name.</summary>
    public static int ParseDamageType(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return -1;
        }

        foreach ((string n, int idx) in DamageTypes)
        {
            if (string.Equals(name.Trim(), n, StringComparison.OrdinalIgnoreCase))
            {
                return idx;
            }
        }

        return -1;
    }

    /// <summary>
    /// The 11 per-damage-type factors from a record's repeated <c>$Damage Type Factor: "name" f</c>
    /// lines (default 1.0 each). Matches Alpine's clutter/entity parse (tbl.cpp:158-165,359-366).
    /// </summary>
    public static float[] ParseDamageFactors(TblRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var factors = new float[11];
        Array.Fill(factors, 1f);
        foreach (string raw in record.GetAllRaw("Damage Type Factor"))
        {
            IReadOnlyList<string> t = TblValue.Tokenize(raw);
            if (t.Count < 2)
            {
                continue;
            }

            int idx = ParseDamageType(t[0]);
            if (idx >= 0 && idx < factors.Length &&
                float.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                factors[idx] = f;
            }
        }

        return factors;
    }

    /// <summary>Clutter collision mode (0 None, 1 Only Weapons, 2 All) from its <c>$Flags:</c> bits
    /// (tbl.h:191-195): collide_object → All, else collide_weapon → Only Weapons, else None.</summary>
    public static int ClutterCollisionMode(IEnumerable<string> flags)
    {
        var set = flags?.ToList() ?? new List<string>();
        if (set.Any(f => string.Equals(f, "collide_object", StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        return set.Any(f => string.Equals(f, "collide_weapon", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    /// <summary>Entity collision mode (0 None, 1 Only Weapons, 2 All) from its combined
    /// <c>$Flags:</c>/<c>$Flags2:</c> tokens (tbl.h:231-235): no_collide → None, else
    /// collide_player → All, else Only Weapons.</summary>
    public static int EntityCollisionMode(IEnumerable<string> flags)
    {
        var set = flags?.ToList() ?? new List<string>();
        if (set.Any(f => string.Equals(f, "no_collide", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        return set.Any(f => string.Equals(f, "collide_player", StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
    }
}

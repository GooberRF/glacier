using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ged.Core.Assets;

/// <summary>The result of validating a candidate Red Faction install directory (item 7).</summary>
public readonly record struct RfInstallScan(bool Exists, IReadOnlyList<string> Vpps, bool HasAlpine, bool HasCorePackfiles)
{
    /// <summary>Number of <c>.vpp</c> packfiles found in the directory.</summary>
    public int VppCount => Vpps.Count;

    /// <summary>A usable install: the directory exists and contains at least one packfile.</summary>
    public bool Valid => Exists && VppCount > 0;

    /// <summary>Inline validation feedback for the Settings field / wizard.</summary>
    public string StatusText()
    {
        if (!Exists)
        {
            return "✗ directory not found";
        }

        if (VppCount == 0)
        {
            return "✗ no RF packfiles found in this directory — pick the folder containing tables.vpp/maps*.vpp";
        }

        string alpine = HasAlpine ? " (+ alpinefaction.vpp)" : string.Empty;
        return $"✓ found {VppCount} VPP{(VppCount == 1 ? string.Empty : "s")}{alpine}";
    }
}

/// <summary>
/// Validates a candidate RF install directory by scanning for <c>.vpp</c> packfiles —
/// so a wrong path (not the VPP-containing root) is caught with feedback instead of
/// mounting an empty VFS and failing silently (item 7).
/// </summary>
public static class RfInstall
{
    private const string AlpineVpp = "alpinefaction.vpp";

    /// <summary>Scans <paramref name="dir"/> for RF packfiles (empty/missing → an invalid result).</summary>
    public static RfInstallScan Scan(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return new RfInstallScan(false, Array.Empty<string>(), false, false);
        }

        string[] vpps;
        try
        {
            vpps = Directory.EnumerateFiles(dir, "*.vpp")
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            return new RfInstallScan(true, Array.Empty<string>(), false, false);
        }

        bool hasAlpine = vpps.Any(v => v.Equals(AlpineVpp, StringComparison.OrdinalIgnoreCase));
        // The core RF content lives in tables.vpp + maps*.vpp; their presence marks a real install root.
        bool hasCore = vpps.Any(v =>
            v.Equals("tables.vpp", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("maps", StringComparison.OrdinalIgnoreCase));

        return new RfInstallScan(true, vpps, hasAlpine, hasCore);
    }
}

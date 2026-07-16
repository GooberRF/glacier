namespace Ged.Core.Editing;

/// <summary>
/// A compatibility target for analysis and reporting — <em>not</em> a file save
/// target. GED always writes Alpine v305 (see <see cref="Ged.Core.IO.Rfl.RflFile.UpgradeToAlpine"/>);
/// it never emits a stock file. This enum is retained as the axis the
/// <see cref="FeatureGate"/> and the linter's budget caps analyse against — e.g.
/// "does this level also fit the stock RF v200 engine?" — which stays useful even
/// though every save is Alpine.
/// </summary>
public enum SaveTarget
{
    /// <summary>Stock Red Faction — RFL version 200 (0xC8). A compatibility reference only; GED does not save it.</summary>
    StockRf,

    /// <summary>Alpine Faction — RFL version 305 (0x131), the version GED always writes.</summary>
    Alpine,
}

/// <summary>Version numbers and display names for the compatibility targets.</summary>
public static class SaveTargets
{
    /// <summary>Stock RF header version (decimal 200) — compatibility reference only.</summary>
    public const int StockRfVersion = 0xC8;

    /// <summary>Alpine header version (decimal 305) — the version GED always writes.</summary>
    public const int AlpineVersion = 0x131;

    /// <summary>The first Alpine version (decimal 300); features at or above this are Alpine-only.</summary>
    public const int FirstAlpineVersion = 0x12C;

    public static int VersionOf(SaveTarget target) =>
        target == SaveTarget.Alpine ? AlpineVersion : StockRfVersion;

    /// <summary>The save target a header version belongs to (300+ ⇒ Alpine).</summary>
    public static SaveTarget FromVersion(int version) =>
        version >= FirstAlpineVersion ? SaveTarget.Alpine : SaveTarget.StockRf;

    public static string DisplayName(SaveTarget target) =>
        target == SaveTarget.Alpine ? "Alpine (v305)" : "Stock RF (v200, compatibility)";
}

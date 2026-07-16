using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ged.Core.Packaging;

/// <summary>One reviewable row in the packfile builder: a dependency plus its include toggle.</summary>
public sealed class PackfileBuildItem
{
    internal PackfileBuildItem(PackDependency dependency, bool include)
    {
        Dependency = dependency;
        Include = include;
    }

    public PackDependency Dependency { get; }

    /// <summary>Whether this file is checked for inclusion (missing files can never be packed).</summary>
    public bool Include { get; set; }

    public string FileName => Dependency.FileName;

    public DependencyKind Kind => Dependency.Kind;

    public DependencyStatus Status => Dependency.Status;

    public long Size => Dependency.Size;

    public IReadOnlyList<string> Origins => Dependency.Origins;

    /// <summary>Missing files cannot be toggled into the pack — they have no bytes.</summary>
    public bool CanInclude => Dependency.Status != DependencyStatus.Missing && Dependency.Read is not null;

    /// <summary>True when this row will actually be written to the VPP.</summary>
    public bool WillPack => Include && CanInclude;
}

/// <summary>A review-tree group of build items sharing one dependency kind.</summary>
public sealed class PackfileBuildGroup
{
    public PackfileBuildGroup(DependencyKind kind, IReadOnlyList<PackfileBuildItem> items)
    {
        Kind = kind;
        Items = items;
    }

    public DependencyKind Kind { get; }

    public IReadOnlyList<PackfileBuildItem> Items { get; }

    public int WillPackCount => Items.Count(i => i.WillPack);

    public long WillPackSize => Items.Where(i => i.WillPack).Sum(i => i.Size);
}

/// <summary>
/// The pure view-model behind the Create-Level-Packfile dialog: it wraps a
/// <see cref="DependencyScanResult"/> as a per-kind reviewable tree with default
/// include selections (loose files on, base-game/missing off), a block-on-missing
/// toggle, a live selected-size total, a default output path, and the selection
/// handed to <see cref="PackfileBuilder"/>. Framework-free so it is unit-testable.
/// </summary>
public sealed class PackfileBuildPlan
{
    public PackfileBuildPlan(DependencyScanResult scan, string levelFileName, string outputPath)
    {
        Scan = scan ?? throw new ArgumentNullException(nameof(scan));
        LevelFileName = levelFileName ?? throw new ArgumentNullException(nameof(levelFileName));
        OutputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));

        var items = scan.All.Select(d => new PackfileBuildItem(d, include: d.Status == DependencyStatus.Included)).ToList();
        AllItems = items;
        Groups = items
            .GroupBy(i => i.Kind)
            .OrderBy(g => (int)g.Key)
            .Select(g => new PackfileBuildGroup(g.Key, g.ToList()))
            .ToList();
    }

    public DependencyScanResult Scan { get; }

    public string LevelFileName { get; }

    /// <summary>Destination VPP path; defaults to <c>user_maps\&lt;mode&gt;\&lt;level&gt;.vpp</c>.</summary>
    public string OutputPath { get; set; }

    /// <summary>When true (default), the build is blocked while any dependency is missing.</summary>
    public bool BlockOnMissing { get; set; } = true;

    public IReadOnlyList<PackfileBuildItem> AllItems { get; }

    public IReadOnlyList<PackfileBuildGroup> Groups { get; }

    /// <summary>The dependencies that will be written, in <see cref="DependencyScanResult.All"/> order.</summary>
    public IEnumerable<PackDependency> Selection => AllItems.Where(i => i.WillPack).Select(i => i.Dependency);

    public int SelectedCount => AllItems.Count(i => i.WillPack);

    public long SelectedSize => AllItems.Where(i => i.WillPack).Sum(i => i.Size);

    public bool HasMissing => Scan.HasMissing;

    /// <summary>False only while missing files remain and the blocking toggle is on.</summary>
    public bool CanBuild => !(BlockOnMissing && HasMissing);

    /// <summary>Runs the build with the current selection (throws if <see cref="CanBuild"/> is false).</summary>
    public PackfileBuildResult Build(byte[] levelRflBytes)
    {
        if (!CanBuild)
        {
            throw new InvalidOperationException("Cannot build: dependencies are missing and blocking is enabled.");
        }

        return PackfileBuilder.Build(levelRflBytes, LevelFileName, Selection, OutputPath);
    }

    /// <summary>
    /// The default output path for a level: <c>&lt;install&gt;\user_maps\&lt;mode&gt;\&lt;level&gt;.vpp</c>
    /// where mode is <c>multi</c> for a multiplayer level, otherwise <c>single</c>.
    /// </summary>
    public static string DefaultOutputPath(string installDir, string levelFileName, bool multiplayer)
    {
        string mode = multiplayer ? "multi" : "single";
        string vpp = Path.GetFileNameWithoutExtension(levelFileName) + ".vpp";
        return Path.Combine(installDir, "user_maps", mode, vpp);
    }
}

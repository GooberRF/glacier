using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Packaging;

/// <summary>
/// The outcome of a <see cref="DependencyScanner.Scan"/>: every dependency
/// partitioned into files that will be packed (<see cref="Included"/>), files the
/// engine already ships and are therefore skipped (<see cref="BaseGameSkipped"/>),
/// and files that could not be resolved (<see cref="Missing"/>), plus size stats.
/// </summary>
public sealed class DependencyScanResult
{
    public DependencyScanResult(IReadOnlyList<PackDependency> all)
    {
        All = all;
        Included = all.Where(d => d.Status == DependencyStatus.Included).ToList();
        BaseGameSkipped = all.Where(d => d.Status == DependencyStatus.BaseGameSkipped).ToList();
        Missing = all.Where(d => d.Status == DependencyStatus.Missing).ToList();
        TotalIncludedSize = Included.Sum(d => d.Size);
    }

    /// <summary>Every scanned dependency, in discovery order.</summary>
    public IReadOnlyList<PackDependency> All { get; }

    /// <summary>Dependencies that resolve from a loose/user mount and will be packed.</summary>
    public IReadOnlyList<PackDependency> Included { get; }

    /// <summary>Dependencies that resolve from a base-game packfile and are skipped.</summary>
    public IReadOnlyList<PackDependency> BaseGameSkipped { get; }

    /// <summary>Dependencies that do not resolve from any mount.</summary>
    public IReadOnlyList<PackDependency> Missing { get; }

    /// <summary>Total byte size of the included files (before VPP alignment padding).</summary>
    public long TotalIncludedSize { get; }

    public bool HasMissing => Missing.Count > 0;

    /// <summary>Distinct included file names (convenience for callers/tests).</summary>
    public IReadOnlyCollection<string> IncludedNames =>
        Included.Select(d => d.FileName).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Distinct skipped file names.</summary>
    public IReadOnlyCollection<string> SkippedNames =>
        BaseGameSkipped.Select(d => d.FileName).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Distinct missing file names.</summary>
    public IReadOnlyCollection<string> MissingNames =>
        Missing.Select(d => d.FileName).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
}

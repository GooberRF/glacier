using System;
using System.Collections.Generic;
using System.Text;
using Ged.Core.IO.Vpp;

namespace Ged.Core.Packaging;

/// <summary>The result of writing a level packfile: what got packed and what was dropped, with reasons.</summary>
public sealed class PackfileBuildResult
{
    public required string OutputPath { get; init; }

    /// <summary>Bare file names written to the VPP, in order (the level .rfl is first).</summary>
    public required IReadOnlyList<string> PackedFiles { get; init; }

    /// <summary>Files that were requested but could not be read (missing on disk at pack time).</summary>
    public required IReadOnlyList<string> SkippedUnreadable { get; init; }

    /// <summary>Files skipped because their name does not fit the VPP 60-byte name field.</summary>
    public required IReadOnlyList<string> SkippedNameTooLong { get; init; }

    /// <summary>Total logical byte size of the packed files (before VPP alignment padding).</summary>
    public long TotalBytes { get; init; }
}

/// <summary>
/// Assembles a Create-Level-Packfile <c>.vpp</c> via <see cref="VppBuilder"/>. The
/// level <c>.rfl</c> is always the first entry — matching RED-produced level packs,
/// where the engine loads the map by its packfile and expects the level record up
/// front — followed by the selected dependency files. Missing files are skipped
/// gracefully (Alpine behaviour); duplicate names and over-long names are dropped
/// with a reason rather than aborting the build.
/// </summary>
public static class PackfileBuilder
{
    /// <summary>
    /// Writes <paramref name="outputPath"/> containing the level record first, then
    /// the readable, selected <paramref name="dependencies"/> (deduplicated by name).
    /// </summary>
    public static PackfileBuildResult Build(
        byte[] levelRflBytes,
        string levelFileName,
        IEnumerable<PackDependency> dependencies,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(levelRflBytes);
        ArgumentException.ThrowIfNullOrEmpty(levelFileName);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var builder = new VppBuilder();
        var packed = new List<string>();
        var unreadable = new List<string>();
        var tooLong = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        // Level record first.
        if (!FitsNameField(levelFileName))
        {
            throw new ArgumentException($"Level file name '{levelFileName}' is too long for a VPP entry.", nameof(levelFileName));
        }

        builder.Add(levelFileName, levelRflBytes);
        seen.Add(levelFileName);
        packed.Add(levelFileName);
        total += levelRflBytes.Length;

        foreach (PackDependency dep in dependencies)
        {
            string name = dep.FileName;
            if (seen.Contains(name))
            {
                continue; // already packed (dedupe; e.g. shared texture)
            }

            if (!FitsNameField(name))
            {
                tooLong.Add(name);
                continue;
            }

            byte[]? data = dep.Read?.Invoke();
            if (data is null)
            {
                unreadable.Add(name);
                continue;
            }

            builder.Add(name, data);
            seen.Add(name);
            packed.Add(name);
            total += data.Length;
        }

        builder.Write(outputPath);
        return new PackfileBuildResult
        {
            OutputPath = outputPath,
            PackedFiles = packed,
            SkippedUnreadable = unreadable,
            SkippedNameTooLong = tooLong,
            TotalBytes = total,
        };
    }

    private static bool FitsNameField(string name) =>
        Encoding.Latin1.GetByteCount(name) < VppFormat.NameFieldSize;
}

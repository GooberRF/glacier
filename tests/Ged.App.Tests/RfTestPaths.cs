using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ged.App.Tests;

/// <summary>
/// Locates a real Red Faction install for the App-layer render tests (read-only). Resolution
/// order: the <c>GED_RF_DIR</c> environment variable first, then each non-comment line of the
/// developer-local, gitignored <c>research/rf-dirs.txt</c> (one path per line, '#' comments
/// allowed). No machine-specific paths are baked into source, so a public checkout resolves
/// nothing and the dependent tests skip gracefully. This is the single shared reader for the
/// App test assembly.
/// </summary>
internal static class RfTestPaths
{
    /// <summary>Root of a real RF install (contains at least one .vpp), or null when none is found.</summary>
    public static string? LocateRfInstall()
    {
        foreach (string dir in RfDirCandidates())
        {
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.vpp").Any())
            {
                return dir;
            }
        }

        return null;
    }

    private static IEnumerable<string> RfDirCandidates()
    {
        string? env = Environment.GetEnvironmentVariable("GED_RF_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        foreach (string line in ReadRfDirsFile())
        {
            yield return line;
        }
    }

    private static IReadOnlyList<string> ReadRfDirsFile()
    {
        var result = new List<string>();
        string? root = LocateRepoRoot();
        if (root is null)
        {
            return result;
        }

        string path = Path.Combine(root, "research", "rf-dirs.txt");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                result.Add(trimmed);
            }
        }
        catch
        {
            // Unreadable rf-dirs.txt -> no candidates; dependent tests skip gracefully.
        }

        return result;
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

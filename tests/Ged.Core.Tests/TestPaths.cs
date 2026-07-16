using System.IO;

namespace Ged.Core.Tests;

/// <summary>
/// Resolves on-disk test resources by walking up from the test binary to the repo
/// root (identified by <c>Glacier.sln</c>). Committed fixtures live under
/// <c>tests/fixtures</c>; the read-only research corpus under <c>research</c>. The
/// optional real RF install is located via the <c>GED_RF_DIR</c> environment
/// variable or the developer-local <c>research/rf-dirs.txt</c>, and is always
/// treated read-only.
/// </summary>
public static class TestPaths
{
    public static string? RepoRoot { get; } = LocateRepoRoot();

    /// <summary>Committed fixtures directory (<c>tests/fixtures</c>), or null if absent.</summary>
    public static string? Fixtures { get; } = Combine("tests", "fixtures");

    /// <summary>
    /// Read-only research fixtures (<c>research/fixtures</c>) holding retail-derived binaries
    /// that are kept out of the public repo (gitignored), or null if absent.
    /// </summary>
    public static string? ResearchFixtures { get; } = Combine("research", "fixtures");

    /// <summary>Read-only research tree (<c>research</c>), or null if absent.</summary>
    public static string? Research { get; } = Combine("research");

    /// <summary>Game table copies used by the tables tests (<c>research/rf_decomp/tables</c>).</summary>
    public static string? Tables { get; } = Combine("research", "rf_decomp", "tables");

    /// <summary>Root of a real Red Faction install (read-only), or null if not found.</summary>
    public static string? RfInstall { get; } = LocateRfInstall();

    public static bool HasFixtures => Fixtures is not null;

    public static bool HasRfInstall => RfInstall is not null;

    /// <summary>Absolute path to a committed texture/mesh fixture, e.g. <c>Fixture("tex", "a.tga")</c>.</summary>
    public static string Fixture(params string[] parts)
    {
        if (Fixtures is null)
        {
            throw new InvalidOperationException("Fixtures directory not found.");
        }

        var all = new string[parts.Length + 1];
        all[0] = Fixtures;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    /// <summary>
    /// Resolves a fixture file, preferring the committed <c>tests/fixtures</c> tree (synthetic
    /// assets) and falling back to <c>research/fixtures</c> (retail-derived binaries kept out of
    /// the public repo). Returns null when the file is in neither root, so a test that needs a
    /// retail-derived fixture can skip gracefully on a checkout that lacks it.
    /// </summary>
    public static string? FixtureFile(params string[] parts)
    {
        foreach (string? root in new[] { Fixtures, ResearchFixtures })
        {
            if (root is null)
            {
                continue;
            }

            var all = new string[parts.Length + 1];
            all[0] = root;
            Array.Copy(parts, 0, all, 1, parts.Length);
            string p = Path.Combine(all);
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>Absolute path to a VPP in the real RF install, or null when the install/file is absent.</summary>
    public static string? RfVpp(string name)
    {
        if (RfInstall is null)
        {
            return null;
        }

        string p = Path.Combine(RfInstall, name);
        return File.Exists(p) ? p : null;
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

    private static string? Combine(params string[] parts)
    {
        if (RepoRoot is null)
        {
            return null;
        }

        var all = new string[parts.Length + 1];
        all[0] = RepoRoot;
        Array.Copy(parts, 0, all, 1, parts.Length);
        string path = Path.Combine(all);
        return Directory.Exists(path) ? path : null;
    }

    private static string? LocateRfInstall()
    {
        foreach (string c in RfDirCandidates())
        {
            if (Directory.Exists(c) && HasAnyVpp(c))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>
    /// Candidate RF-install directories in priority order: the <c>GED_RF_DIR</c> environment
    /// variable first, then each non-comment line of the developer-local, gitignored
    /// <c>research/rf-dirs.txt</c> (one path per line, '#' comments allowed). No machine-specific
    /// paths are baked into source, so a public checkout resolves nothing and RF-dependent tests
    /// skip gracefully.
    /// </summary>
    internal static IEnumerable<string> RfDirCandidates()
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
        if (RepoRoot is null)
        {
            return result;
        }

        string path = Path.Combine(RepoRoot, "research", "rf-dirs.txt");
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
            // Unreadable rf-dirs.txt -> no candidates; RF-dependent tests skip gracefully.
        }

        return result;
    }

    private static bool HasAnyVpp(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.vpp").Any();
        }
        catch
        {
            return false;
        }
    }
}

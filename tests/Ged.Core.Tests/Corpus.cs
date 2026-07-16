using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ged.Core.Tests;

/// <summary>
/// Locates the read-only example-level corpus (research/example_rfls) by walking
/// up from the test binary to the repo root. Tests degrade gracefully to a
/// single no-op case when the corpus is absent (e.g. a clean CI checkout that
/// does not include the untracked corpus).
/// </summary>
public static class Corpus
{
    public static string? Directory { get; } = Locate();

    public static bool Available => Directory is not null;

    private static string? Locate()
    {
        // Explicit override (used when the tests run from an isolated git worktree whose
        // checkout does not carry the untracked research/example_rfls corpus).
        string? env = System.Environment.GetEnvironmentVariable("GED_CORPUS_DIR");
        if (!string.IsNullOrEmpty(env) && System.IO.Directory.Exists(env))
        {
            return env;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
            {
                string candidate = Path.Combine(dir.FullName, "research", "example_rfls");
                return System.IO.Directory.Exists(candidate) ? candidate : null;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static IReadOnlyList<string> RflFiles =>
        Directory is null
            ? Array.Empty<string>()
            : System.IO.Directory.GetFiles(Directory, "*.rfl").OrderBy(p => p).ToArray();

    /// <summary>
    /// xUnit MemberData source: one row per corpus .rfl file name, or a single
    /// null-sentinel row when the corpus is unavailable so the theory still
    /// yields a (trivially passing) case.
    /// </summary>
    public static IEnumerable<object?[]> RflFileNames()
    {
        if (!Available)
        {
            yield return new object?[] { null };
            yield break;
        }

        foreach (string path in RflFiles)
        {
            yield return new object?[] { Path.GetFileName(path) };
        }
    }
}

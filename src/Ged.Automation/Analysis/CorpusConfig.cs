using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Analysis;

namespace Ged.Automation.Analysis;

/// <summary>
/// Where the corpus lives (CORPUS-NORMS-SPEC §2.1): roots are configured, never hard-coded — explicit roots
/// (<c>--root</c>), else <c>GED_CORPUS_DIRS</c> (<c>;</c>-separated), else <c>research/corpus-dirs.txt</c> (one per line,
/// <c>#</c> comments). An RF install contributes its top-level packfiles; it is resolved like the tests do
/// (<c>--rf-dir</c>, else <c>GED_RF_DIR</c>, else the first <c>research/rf-dirs.txt</c> line holding packfiles) and is
/// only included implicitly when the roots also come from the configuration. Every root is read-only.
/// </summary>
public sealed class CorpusConfig
{
    private CorpusConfig(IReadOnlyList<string> roots, IReadOnlyList<string> missing, string? rfInstall, IReadOnlyDictionary<string, string> aliases)
    {
        Roots = roots;
        MissingRoots = missing;
        RfInstall = rfInstall;
        AuthorAliases = aliases;
    }

    /// <summary>
    /// The environment variable naming the curated exemplar list for <c>corpus aggregate</c> (else
    /// <c>research/exemplar-levels.txt</c> in the repository checkout; missing ⇒ no exemplar bucket).
    /// </summary>
    public const string ExemplarListVariable = "GED_EXEMPLAR_LEVELS";

    /// <summary>The existing corpus roots, full paths, in configured order (duplicates removed).</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Configured roots that do not exist (a warning, not an error).</summary>
    public IReadOnlyList<string> MissingRoots { get; }

    /// <summary>The RF install whose top-level packfiles are swept (full path), or null.</summary>
    public string? RfInstall { get; }

    /// <summary>Normalised author → canonical author (<c>research/corpus-author-aliases.txt</c>: <c>alias = canonical</c> lines).</summary>
    public IReadOnlyDictionary<string, string> AuthorAliases { get; }

    /// <summary>Every directory the sweep reads from (roots + the install): the output directory must stay clear of all of them.</summary>
    public IReadOnlyList<string> ProtectedRoots =>
        RfInstall is null ? Roots : Roots.Append(RfInstall).ToList();

    /// <summary>A configuration with exactly these roots and install (tests, and callers that resolved paths themselves).</summary>
    public static CorpusConfig Explicit(IEnumerable<string> roots, string? rfInstall = null, IReadOnlyDictionary<string, string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        (List<string> existing, List<string> missing) = Split(roots);
        string? install = rfInstall is not null && Directory.Exists(rfInstall) ? Normalize(rfInstall) : null;
        return new CorpusConfig(existing, missing, install, aliases ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Resolves the configuration: <paramref name="explicitRoots"/> win over <c>GED_CORPUS_DIRS</c>, which wins over
    /// <c>research/corpus-dirs.txt</c> under <paramref name="repoRoot"/>. The install is <paramref name="explicitRfDir"/>
    /// when given; otherwise (and only when the roots came from the configuration, unless <paramref name="noInstall"/>)
    /// <c>GED_RF_DIR</c> or <c>research/rf-dirs.txt</c>.
    /// </summary>
    public static CorpusConfig Resolve(
        IReadOnlyList<string>? explicitRoots,
        string? explicitRfDir,
        bool noInstall,
        Func<string, string?> getEnvironmentVariable,
        string? repoRoot)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        bool fromConfig = explicitRoots is not { Count: > 0 };
        IEnumerable<string> roots = !fromConfig
            ? explicitRoots!
            : getEnvironmentVariable("GED_CORPUS_DIRS") is { Length: > 0 } env
            ? env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ReadList(repoRoot, "corpus-dirs.txt");

        string? install = null;
        if (explicitRfDir is not null)
        {
            install = explicitRfDir;
        }
        else if (fromConfig && !noInstall)
        {
            var candidates = new List<string>();
            if (getEnvironmentVariable("GED_RF_DIR") is { Length: > 0 } rf)
            {
                candidates.Add(rf);
            }

            candidates.AddRange(ReadList(repoRoot, "rf-dirs.txt"));
            install = candidates.FirstOrDefault(c => Directory.Exists(c) && Directory.EnumerateFiles(c, "*.vpp").Any());
        }

        if (noInstall)
        {
            install = null;
        }

        (List<string> existing, List<string> missing) = Split(roots);
        string? installFull = install is not null && Directory.Exists(install) ? Normalize(install) : null;
        return new CorpusConfig(existing, missing, installFull, ReadAliases(repoRoot));
    }

    /// <summary>A full path without a trailing separator.</summary>
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));

    private static (List<string> Existing, List<string> Missing) Split(IEnumerable<string> roots)
    {
        var existing = new List<string>();
        var missing = new List<string>();
        foreach (string raw in roots)
        {
            string trimmed = raw.Trim().Trim('﻿').Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            string full = Normalize(trimmed);
            List<string> target = Directory.Exists(full) ? existing : missing;
            if (!target.Contains(full, CorpusOutput.PathComparer))
            {
                target.Add(full);
            }
        }

        return (existing, missing);
    }

    private static IEnumerable<string> ReadList(string? repoRoot, string fileName)
    {
        string? path = repoRoot is null ? null : Path.Combine(repoRoot, "research", fileName);
        if (path is null || !File.Exists(path))
        {
            return Array.Empty<string>();
        }

        try
        {
            return File.ReadAllLines(path)
                .Select(l => l.Trim().Trim('﻿').Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToList();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyDictionary<string, string> ReadAliases(string? repoRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in ReadList(repoRoot, "corpus-author-aliases.txt"))
        {
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string alias = LevelIdentity.NormalizeAuthor(line[..eq]);
            string canon = LevelIdentity.NormalizeAuthor(line[(eq + 1)..]);
            if (alias.Length > 0 && canon.Length > 0)
            {
                map[alias] = canon;
            }
        }

        return map;
    }

    /// <summary>The repository root (the folder holding Glacier.sln) above <paramref name="start"/>, or null.</summary>
    public static string? FindRepoRoot(string? start)
    {
        var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
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

    /// <summary>
    /// The default output directory: <c>&lt;repo&gt;/artifacts/corpus</c> when running from a checkout (gitignored), else
    /// <c>%LOCALAPPDATA%/Glacier/corpus</c>.
    /// </summary>
    public static string DefaultOutputDir(string? repoRoot) =>
        repoRoot is not null
            ? Path.Combine(repoRoot, "artifacts", "corpus")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glacier", "corpus");
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ged.Automation.Analysis;

/// <summary>
/// The ONLY class in the analysis code that writes files (CORPUS-NORMS-SPEC §2.3). It owns one output directory and
/// refuses — before anything is created — an output directory that equals, lies inside, or contains any corpus root
/// or the RF install. Every write is atomic (a <c>*.tmp</c> file in the same output folder, then a move) and must
/// resolve inside the output directory.
/// </summary>
public sealed class CorpusOutput
{
    /// <summary>Path comparison for the guard: case-insensitive on Windows, ordinal elsewhere.</summary>
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IReadOnlyList<string> _protected;

    /// <summary>Validates <paramref name="outputDir"/> against <paramref name="protectedRoots"/>, then creates it.</summary>
    /// <exception cref="AutomationException">Code <c>usage</c> when the output directory overlaps a protected root.</exception>
    public CorpusOutput(string outputDir, IEnumerable<string> protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(outputDir);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        _protected = protectedRoots.Select(CorpusConfig.Normalize).ToList();
        Root = CorpusConfig.Normalize(outputDir);
        Guard(Root, _protected);
        Directory.CreateDirectory(Root);
    }

    /// <summary>The output directory (full path).</summary>
    public string Root { get; }

    /// <summary>
    /// Throws when <paramref name="outputDir"/> equals, is inside, or contains any of <paramref name="roots"/> (full
    /// paths, case-insensitive on Windows), or is itself a reparse point.
    /// </summary>
    public static void Guard(string outputDir, IEnumerable<string> roots)
    {
        string o = CorpusConfig.Normalize(outputDir);
        foreach (string r in roots.Select(CorpusConfig.Normalize))
        {
            if (string.Equals(o, r, PathComparison))
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"The output directory '{o}' is a corpus root; choose a folder outside every corpus root (--out).");
            }

            if (IsInside(o, r))
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"The output directory '{o}' is inside the corpus root '{r}'; choose a folder outside every corpus root (--out).");
            }

            if (IsInside(r, o))
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"The output directory '{o}' contains the corpus root '{r}'; choose a folder outside every corpus root (--out).");
            }
        }

        if (Directory.Exists(o) && (new DirectoryInfo(o).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"The output directory '{o}' is a link (junction or symbolic link); use a real folder.");
        }
    }

    /// <summary>True when <paramref name="path"/> lies strictly inside <paramref name="dir"/>.</summary>
    public static bool IsInside(string path, string dir)
    {
        string d = Path.TrimEndingDirectorySeparator(dir);
        return path.Length > d.Length + 1
            && path.StartsWith(d, PathComparison)
            && (path[d.Length] == Path.DirectorySeparatorChar || path[d.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>The full path of an output-relative path (validated to stay inside the output directory).</summary>
    public string PathOf(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        string full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!IsInside(full, Root))
        {
            throw new AutomationException(AutomationErrorCodes.PathNotAllowed, $"'{relativePath}' is outside the output directory.");
        }

        foreach (string r in _protected)
        {
            if (string.Equals(full, r, PathComparison) || IsInside(full, r))
            {
                throw new AutomationException(AutomationErrorCodes.PathNotAllowed, $"'{full}' is inside a corpus root.");
            }
        }

        return full;
    }

    /// <summary>True when the output-relative file exists.</summary>
    public bool Exists(string relativePath) => File.Exists(PathOf(relativePath));

    /// <summary>Reads back an output-relative text file (the output directory's own data, e.g. a cached fingerprint).</summary>
    public string ReadText(string relativePath) => File.ReadAllText(PathOf(relativePath), Utf8);

    /// <summary>Writes UTF-8 text (no BOM) atomically.</summary>
    public void WriteText(string relativePath, string text) => WriteBytes(relativePath, Utf8.GetBytes(text ?? string.Empty));

    /// <summary>Writes one line per item (LF endings) atomically.</summary>
    public void WriteLines(string relativePath, IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (string l in lines)
        {
            sb.Append(l).Append('\n');
        }

        WriteText(relativePath, sb.ToString());
    }

    /// <summary>Writes bytes atomically: a temporary file next to the target, then a move over it.</summary>
    public void WriteBytes(string relativePath, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string full = PathOf(relativePath);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }
}

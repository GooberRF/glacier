using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Ged.Core.IO.Vpp;

namespace Ged.Automation.Analysis;

/// <summary>One file found under a corpus root.</summary>
/// <param name="Root">The root it was found under (full path).</param>
/// <param name="FullPath">The file's full path.</param>
/// <param name="RelativePath">The path relative to <paramref name="Root"/>, '/'-separated.</param>
public sealed record CorpusFile(string Root, string FullPath, string RelativePath, long Length, DateTime LastWriteUtc);

/// <summary>
/// Read-only corpus access (CORPUS-NORMS-SPEC §2.2): the ONLY way the analysis code touches corpus bytes. Every
/// stream is opened <c>FileMode.Open, FileAccess.Read, FileShare.Read</c>; enumeration never follows reparse points
/// (a junction must never widen a sweep). Nothing here creates, moves, deletes or re-attributes a file.
/// </summary>
public static class CorpusReader
{
    /// <summary>Opens a corpus file for reading only.</summary>
    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

    /// <summary>Reads a whole corpus file (read-only stream).</summary>
    public static byte[] ReadAll(string path)
    {
        using FileStream fs = OpenRead(path);
        long len = fs.Length;
        if (len > Array.MaxLength)
        {
            throw new IOException($"File too large to read: {len} bytes.");
        }

        var buffer = new byte[len];
        fs.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Opens a packfile over a read-only stream (closed with the archive).</summary>
    public static VppArchive OpenVpp(string path)
    {
        FileStream fs = OpenRead(path);
        try
        {
            return VppArchive.Open(fs, leaveOpen: false);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Every file under <paramref name="root"/>, recursively, in ordinal relative-path order; directories that are
    /// reparse points (junctions, symbolic links) are not entered and are reported through
    /// <paramref name="skippedReparsePoints"/>; reparse-point files are skipped the same way. Inaccessible directories
    /// are skipped.
    /// </summary>
    public static List<CorpusFile> EnumerateFiles(string root, CancellationToken ct, List<string>? directories = null, List<string>? skippedReparsePoints = null)
    {
        var files = new List<CorpusFile>();
        var stack = new Stack<string>();
        stack.Push(root);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (FileSystemInfo e in entries)
            {
                string rel = Relative(root, e.FullName);
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skippedReparsePoints?.Add(rel);
                    continue;
                }

                if (e is DirectoryInfo)
                {
                    directories?.Add(rel);
                    stack.Push(e.FullName);
                }
                else if (e is FileInfo fi)
                {
                    files.Add(new CorpusFile(root, fi.FullName, rel, fi.Length, fi.LastWriteTimeUtc));
                }
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        directories?.Sort(StringComparer.Ordinal);
        skippedReparsePoints?.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>The top-level files of <paramref name="dir"/> matching <paramref name="pattern"/> (no recursion), ordinal order.</summary>
    public static List<CorpusFile> TopLevelFiles(string dir, string pattern)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, MatchCasing = MatchCasing.CaseInsensitive };
        return new DirectoryInfo(dir).EnumerateFiles(pattern, options)
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(f => new CorpusFile(dir, f.FullName, f.Name, f.Length, f.LastWriteTimeUtc))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A '/'-separated path of <paramref name="full"/> relative to <paramref name="root"/>.</summary>
    public static string Relative(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');
}

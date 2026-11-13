using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ged.Automation;

/// <summary>
/// Where automation commands may write files (opening is limited to .rfl level files,
/// see <see cref="AutomationCommands"/>). A path is allowed
/// when it lies under one of the allowed folders — the open level's folder, the RF install's
/// <c>user_maps</c>, the scripts library — or is one of the exact paths the user typed on the command
/// line (headless runs only; requests arriving over the editor bridge never add explicit paths).
/// Paths are compared fully-resolved (so <c>..</c> segments cannot escape a folder) and
/// case-insensitively on Windows.
/// </summary>
public sealed class PathAllowList
{
    private static readonly StringComparison Cmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Func<IEnumerable<string?>> _roots;
    private readonly List<string> _explicit = new();
    private readonly List<string> _extraRoots = new();

    /// <param name="roots">Evaluated per check, so the allowed folders follow the open level and install.</param>
    public PathAllowList(Func<IEnumerable<string?>> roots) => _roots = roots ?? throw new ArgumentNullException(nameof(roots));

    /// <summary>The currently allowed folders (resolved, de-duplicated, existing or not).</summary>
    public IReadOnlyList<string> Roots =>
        _roots()
            .Concat(_extraRoots)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Normalize(r!))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Adds a further allowed folder (a scratch folder: a host-chosen working directory for
    /// generated output). Never driven by bridge requests.
    /// </summary>
    public void AddRoot(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder))
        {
            _extraRoots.Add(folder);
        }
    }

    /// <summary>Exact files the user named on the command line (headless).</summary>
    public IReadOnlyList<string> ExplicitPaths => _explicit;

    /// <summary>Allows one exact file path (a headless <c>--out</c> / <c>--save</c> / level argument).</summary>
    public void AllowExplicit(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            string full = Normalize(path);
            if (!_explicit.Any(p => string.Equals(p, full, Cmp)))
            {
                _explicit.Add(full);
            }
        }
    }

    /// <summary>Exact files the user allowed scripts to READ (headless <c>--allow-import</c>); never writable.</summary>
    public IReadOnlyList<string> ExplicitReadPaths => _explicitRead;

    private readonly List<string> _explicitRead = new();

    /// <summary>
    /// Allows scripts to read one exact file (a headless <c>--allow-import &lt;file&gt;</c>). Unlike
    /// <see cref="AllowExplicit"/> this never makes the path a write target.
    /// </summary>
    public void AllowExplicitRead(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            string full = Normalize(path);
            if (!_explicitRead.Any(p => string.Equals(p, full, Cmp)))
            {
                _explicitRead.Add(full);
            }
        }
    }

    /// <summary>True when scripts may read <paramref name="path"/>: allowed for writing, or named with <see cref="AllowExplicitRead"/>.</summary>
    public bool IsReadAllowed(string path) =>
        !string.IsNullOrWhiteSpace(path) && (IsAllowed(path) || _explicitRead.Any(p => string.Equals(p, Normalize(path), Cmp)));

    /// <summary>
    /// The script-facing read check (<c>level.import_mesh</c>): returns the resolved path, or throws a
    /// <see cref="Ged.Core.Scripting.ScriptApiException"/> naming the allowed folders.
    /// </summary>
    public string RequireScriptRead(string path, string api)
    {
        string full = Normalize(path);
        if (IsReadAllowed(full))
        {
            return full;
        }

        IReadOnlyList<string> roots = Roots;
        string where = roots.Count == 0 ? "no folders are currently allowed" : "allowed folders: " + string.Join("; ", roots);
        throw new Ged.Core.Scripting.ScriptApiException(
            $"{api}: refusing to read '{full}': it is outside the allowed folders ({where}).",
            "Put the model next to the level, in user_maps or in <scripts>/import, or pass --allow-import <file> to glacier-cli.");
    }

    /// <summary>
    /// The script-facing write check for files a script writes besides the level (the .v3m of
    /// <c>level.import_mesh{as = "mesh"}</c>): returns the resolved path or throws a
    /// <see cref="Ged.Core.Scripting.ScriptApiException"/>.
    /// </summary>
    public string RequireScriptWrite(string path, string api)
    {
        string full = Normalize(path);
        if (IsAllowed(full))
        {
            return full;
        }

        throw new Ged.Core.Scripting.ScriptApiException(
            $"{api}: refusing to write '{full}': scripts may only write under the level's folder, user_maps, or the scripts library.",
            "Mount the RF install whose user_maps folder should receive the mesh.");
    }

    /// <summary>True when <paramref name="path"/> was named explicitly by the user.</summary>
    public bool IsExplicit(string path) => _explicit.Any(p => string.Equals(p, Normalize(path), Cmp));

    /// <summary>True when <paramref name="path"/> is explicit or lies under an allowed folder.</summary>
    public bool IsAllowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full = Normalize(path);
        if (_explicit.Any(p => string.Equals(p, full, Cmp)))
        {
            return true;
        }

        foreach (string root in Roots)
        {
            string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, Cmp))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Throws an <see cref="AutomationException"/> (<c>path_not_allowed</c>) unless allowed.</summary>
    public string Require(string path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"A path is required to {purpose}.");
        }

        string full = Normalize(path);
        if (!IsAllowed(full))
        {
            IReadOnlyList<string> roots = Roots;
            string where = roots.Count == 0 ? "no folders are currently allowed" : "allowed folders: " + string.Join("; ", roots);
            throw new AutomationException(
                AutomationErrorCodes.PathNotAllowed,
                $"Refusing to {purpose} '{full}': it is outside the allowed folders ({where}).");
        }

        return full;
    }

    /// <summary>Fully resolves a path (relative paths against the current directory).</summary>
    public static string Normalize(string path) => Path.GetFullPath(path.Trim());
}

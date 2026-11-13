using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Ged.Core.Scripting;

/// <summary>One resolved Lua module: its name, source text, the chunk name errors report, and where it came from.</summary>
public sealed record ScriptModuleSource(string Name, string Source, string ChunkName, bool IsShipped, string? FilePath);

/// <summary>
/// Resolves <c>ged.import(name)</c> without any engine dependency. Names are restricted to
/// <c>^[a-z][a-z0-9_]{0,63}$</c> — no paths, no dots — so traversal outside the module folder is
/// impossible by construction. Resolution order:
/// <list type="number">
/// <item>Shipped modules embedded in this assembly (<c>Ged.Core.Scripting.Lib.&lt;name&gt;.lua</c>, built from
/// the repo's <c>scripts/lib/*.lua</c>). A shipped name ALWAYS resolves to the embedded copy, so a stale
/// file seeded into a user's scripts folder can never shadow a newer build.</item>
/// <item>A user module <c>&lt;moduleDirectory&gt;/&lt;name&gt;.lua</c> (default <c>&lt;scripts&gt;/lib</c>).</item>
/// <item>Otherwise an "Unknown module" error listing what is available.</item>
/// </list>
/// </summary>
public static class ScriptModuleResolver
{
    /// <summary>The manifest-resource prefix of the embedded shipped modules.</summary>
    public const string ResourcePrefix = "Ged.Core.Scripting.Lib.";

    /// <summary>The library sub-folder (relative to the scripts folder) that holds modules.</summary>
    public const string LibFolder = "lib";

    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Shipped = new(LoadShipped);

    /// <summary>
    /// Windows reserved device names: <c>con.lua</c>, <c>nul.lua</c>, <c>com1.lua</c>… can open the
    /// device rather than a file, so they are never module names on any platform.
    /// </summary>
    private static readonly Regex DeviceName = new("^(con|prn|aux|nul|com[0-9]|lpt[0-9])$", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="name"/> is a valid module name (pattern above, and not a reserved device name).</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name) && !DeviceName.IsMatch(name);

    /// <summary>The names of the modules shipped with this build, sorted.</summary>
    public static IReadOnlyList<string> ShippedNames() =>
        Shipped.Value.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>The embedded source of a shipped module, or null when <paramref name="name"/> is not shipped.</summary>
    public static string? ShippedSource(string name) =>
        Shipped.Value.TryGetValue(name, out string? source) ? source : null;

    /// <summary>The default user module folder: <c>&lt;scripts&gt;/lib</c>.</summary>
    public static string DefaultModuleDirectory => Path.Combine(AppPaths.ScriptsDirectory, LibFolder);

    /// <summary>Every importable module name (shipped first-class, plus valid user module files), sorted.</summary>
    public static IReadOnlyList<string> AvailableNames(string? moduleDirectory)
    {
        var names = new SortedSet<string>(Shipped.Value.Keys, StringComparer.Ordinal);
        string dir = moduleDirectory ?? DefaultModuleDirectory;
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.lua", SearchOption.TopDirectoryOnly))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (IsValidName(stem))
                    {
                        names.Add(stem);
                    }
                }
            }
        }
        catch (IOException)
        {
            // An unreadable folder lists only the shipped modules.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return names.ToList();
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to its source. Throws <see cref="ScriptApiException"/> for an
    /// invalid name or an unknown module (with the available names as the hint).
    /// </summary>
    public static ScriptModuleSource Resolve(string? name, string? moduleDirectory)
    {
        if (!IsValidName(name))
        {
            throw new ScriptApiException($"Invalid module name '{name}'.",
                "Module names are lower-case letters, digits and '_' (starting with a letter), with no path or extension — e.g. ged.import(\"kit\").");
        }

        if (ShippedSource(name!) is { } shipped)
        {
            return new ScriptModuleSource(name!, shipped, ChunkNameFor(name!), IsShipped: true, FilePath: null);
        }

        string dir = moduleDirectory ?? DefaultModuleDirectory;
        string path = Path.Combine(dir, name + ".lua");
        if (File.Exists(path))
        {
            string source;
            try
            {
                source = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ScriptApiException($"Module '{name}' could not be read: {ex.Message}");
            }

            return new ScriptModuleSource(name!, source, ChunkNameFor(name!), IsShipped: false, FilePath: path);
        }

        IReadOnlyList<string> available = AvailableNames(moduleDirectory);
        throw new ScriptApiException($"Unknown module '{name}'.",
            $"Available modules: {string.Join(", ", available)}. User modules go in the scripts folder's lib/ as <name>.lua.");
    }

    /// <summary>The chunk name a module's diagnostics report (<c>lib/&lt;name&gt;.lua</c>).</summary>
    public static string ChunkNameFor(string name) => $"{LibFolder}/{name}.lua";

    private static IReadOnlyDictionary<string, string> LoadShipped()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Assembly asm = typeof(ScriptModuleResolver).Assembly;
        foreach (string resource in asm.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".lua", StringComparison.Ordinal))
            {
                continue;
            }

            string name = resource.Substring(ResourcePrefix.Length, resource.Length - ResourcePrefix.Length - ".lua".Length);
            if (!IsValidName(name))
            {
                continue;
            }

            using Stream? stream = asm.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            map[name] = reader.ReadToEnd();
        }

        return map;
    }
}

/// <summary>
/// The engine side of module loading, implemented by the script engine session: runs a
/// module's source as its own chunk in the SAME sandboxed environment as the importing script, under
/// the run's instruction budget / time limit / cancellation, and returns the chunk's return value (an
/// engine value passed back to the script untouched). Caching and circular-import detection live in
/// <see cref="ScriptGed.Import"/>.
/// </summary>
public interface IScriptModuleHost
{
    /// <summary>Executes <paramref name="source"/> as chunk <paramref name="chunkName"/> and returns its value.</summary>
    object? Import(string name, string source, string chunkName);
}

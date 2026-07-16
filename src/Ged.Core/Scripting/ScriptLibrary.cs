using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Input;

namespace Ged.Core.Scripting;

/// <summary>
/// The portable scripts library (plan §6.4/§6.5): enumerates <c>*.lua</c> under
/// <see cref="AppPaths.ScriptsDirectory"/>, parses each file's <c>--@</c> header metadata, and
/// projects the ones that declare a command into <see cref="CommandDefinition"/>s so the App can
/// register them in the palette + keymap. Pure data — no engine dependency — so it is testable
/// without a window or MoonSharp.
/// </summary>
public sealed class ScriptLibrary
{
    public ScriptLibrary(string rootDirectory)
    {
        Root = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
    }

    /// <summary>The library root (the <c>scripts\</c> folder).</summary>
    public string Root { get; }

    /// <summary>The scripts found by the last <see cref="Rescan"/> (empty until scanned).</summary>
    public IReadOnlyList<ScriptLibraryEntry> Entries { get; private set; } = Array.Empty<ScriptLibraryEntry>();

    /// <summary>Enumerates <c>*.lua</c> recursively, parsing metadata. Missing root ⇒ empty (no throw).</summary>
    public void Rescan()
    {
        if (!Directory.Exists(Root))
        {
            Entries = Array.Empty<ScriptLibraryEntry>();
            return;
        }

        var list = new List<ScriptLibraryEntry>();
        foreach (string path in Directory.EnumerateFiles(Root, "*.lua", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ScriptLibraryEntry? entry = TryLoad(path);
            if (entry is not null)
            {
                list.Add(entry);
            }
        }

        Entries = list;
    }

    private ScriptLibraryEntry? TryLoad(string path)
    {
        try
        {
            string source = File.ReadAllText(path);
            ScriptMetadata meta = ScriptMetadata.Parse(source);
            string rel = Path.GetRelativePath(Root, path).Replace('\\', '/');
            return new ScriptLibraryEntry(path, rel, meta, source);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The command definitions for every entry that declares a <c>--@name</c>+<c>--@id</c> header.</summary>
    public IEnumerable<CommandDefinition> CommandDefinitions()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptLibraryEntry e in Entries)
        {
            if (!e.Metadata.IsCommand || !seen.Add(e.CommandId))
            {
                continue;
            }

            yield return new CommandDefinition
            {
                Id = e.CommandId,
                DisplayName = e.Metadata.Name!,
                Category = string.IsNullOrWhiteSpace(e.Metadata.Category) ? "Scripts" : e.Metadata.Category,
                Scope = CommandScope.Global,
                Implemented = true,
            };
        }
    }

    /// <summary>The command-id prefix used for user scripts (<c>script.user.&lt;id&gt;</c>).</summary>
    public const string CommandIdPrefix = "script.user.";
}

/// <summary>One script in the library: its path, header metadata, and source.</summary>
public sealed class ScriptLibraryEntry
{
    internal ScriptLibraryEntry(string path, string relativePath, ScriptMetadata metadata, string source)
    {
        Path = path;
        RelativePath = relativePath;
        Metadata = metadata;
        Source = source;
    }

    /// <summary>The absolute file path.</summary>
    public string Path { get; }

    /// <summary>The path relative to the library root (forward slashes).</summary>
    public string RelativePath { get; }

    public ScriptMetadata Metadata { get; }

    /// <summary>The full source text.</summary>
    public string Source { get; }

    /// <summary>True for a bundled read-only example (under <c>examples/</c>).</summary>
    public bool IsExample => RelativePath.StartsWith("examples/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The command id for a script that declares one (<c>script.user.&lt;id&gt;</c>).</summary>
    public string CommandId => ScriptLibrary.CommandIdPrefix + (Metadata.Id ?? string.Empty);

    /// <summary>A display title: the metadata name, else the file name.</summary>
    public string Title => Metadata.Name ?? System.IO.Path.GetFileNameWithoutExtension(Path);
}

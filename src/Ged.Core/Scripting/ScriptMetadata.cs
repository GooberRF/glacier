using System;
using System.Collections.Generic;

namespace Ged.Core.Scripting;

/// <summary>
/// Metadata parsed from a script's leading <c>--@key value</c> header comments (plan §6.5).
/// A user script with a <c>--@name</c>/<c>--@id</c> becomes a first-class command registered in
/// the palette; <c>--@api</c> pins the API version; <c>--@allow-destructive</c> pre-authorizes
/// delete/overwrite/package/playtest for batch use.
/// </summary>
public sealed class ScriptMetadata
{
    /// <summary><c>--@name</c> — the human-readable command title.</summary>
    public string? Name { get; private set; }

    /// <summary><c>--@id</c> — a stable slug; the command id becomes <c>script.user.&lt;id&gt;</c>.</summary>
    public string? Id { get; private set; }

    /// <summary><c>--@category</c> — palette grouping (default "Scripts").</summary>
    public string Category { get; private set; } = "Scripts";

    /// <summary><c>--@key</c> — a default keybinding gesture (e.g. <c>Ctrl+Shift+A</c>).</summary>
    public string? Key { get; private set; }

    /// <summary><c>--@desc</c> — a one-line description shown in the gallery/palette.</summary>
    public string? Description { get; private set; }

    /// <summary><c>--@api N</c> — the API major version the script targets, or null.</summary>
    public int? ApiVersion { get; private set; }

    /// <summary><c>--@allow-destructive</c> — batch pre-authorization for destructive ops.</summary>
    public bool AllowDestructive { get; private set; }

    /// <summary>True when the header declares enough to register as a command.</summary>
    public bool IsCommand => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Id);

    /// <summary>Parses the leading comment header of <paramref name="source"/>. Stops at the first
    /// non-blank, non-comment line, so directives must sit at the top of the file.</summary>
    public static ScriptMetadata Parse(string? source)
    {
        var meta = new ScriptMetadata();
        if (string.IsNullOrEmpty(source))
        {
            return meta;
        }

        foreach (string rawLine in EnumerateLines(source))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!line.StartsWith("--", StringComparison.Ordinal))
            {
                break; // header ends at first code line
            }

            string body = line[2..].TrimStart();
            if (!body.StartsWith('@'))
            {
                continue; // an ordinary comment inside the header block
            }

            (string key, string value) = SplitDirective(body[1..]);
            Apply(meta, key, value);
        }

        return meta;
    }

    private static void Apply(ScriptMetadata meta, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "name": meta.Name = value; break;
            case "id": meta.Id = Slug(value); break;
            case "category": if (value.Length > 0) { meta.Category = value; } break;
            case "key": meta.Key = value; break;
            case "desc":
            case "description": meta.Description = value; break;
            case "api":
                if (int.TryParse(value, out int v)) { meta.ApiVersion = v; }
                break;
            case "allow-destructive":
            case "allow_destructive":
                meta.AllowDestructive = value.Length == 0 || IsTruthy(value);
                break;
        }
    }

    private static (string Key, string Value) SplitDirective(string body)
    {
        int sp = body.IndexOfAny(new[] { ' ', '\t' });
        return sp < 0 ? (body, string.Empty) : (body[..sp], body[(sp + 1)..].Trim());
    }

    private static bool IsTruthy(string v) =>
        v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("1", StringComparison.Ordinal);

    /// <summary>Lowercases and dashes a value into a stable command slug.</summary>
    public static string Slug(string value)
    {
        var chars = new List<char>(value.Length);
        bool lastDash = false;
        foreach (char c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                chars.Add(c);
                lastDash = false;
            }
            else if (!lastDash && chars.Count > 0)
            {
                chars.Add('-');
                lastDash = true;
            }
        }

        while (chars.Count > 0 && chars[^1] == '-')
        {
            chars.RemoveAt(chars.Count - 1);
        }

        return new string(chars.ToArray());
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                yield return text.Substring(start, i - start).TrimEnd('\r');
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ged.Core.Scripting;

namespace Ged.Automation;

/// <summary>
/// The generated scripting reference as text, for <c>glacier-cli api</c>, the <c>api</c> automation command
/// (which the editor bridge serves too) and the MCP <c>api_reference</c> tool: one entry — a whole global or
/// type (<c>level</c>, <c>brush</c>, <c>kit</c>) or one member (<c>level.brush</c>, <c>brush:set_pos</c>,
/// <c>kit.room</c>) — from the same model <c>ged.describe</c> serves inside scripts, the one-line-per-name
/// <see cref="Index"/>, or the whole Markdown reference.
/// </summary>
public static class ApiReferenceText
{
    /// <summary>
    /// The entry for <paramref name="query"/> as (canonical name, text); an <see cref="AutomationException"/>
    /// (<c>usage</c>) naming the nearest names when unknown.
    /// </summary>
    public static (string Name, string Text) Member(string query)
    {
        string q = (query ?? string.Empty).Trim();
        int sep = q.IndexOfAny(new[] { '.', ':' });
        string receiver = sep < 0 ? q : q[..sep];
        ScriptTypeInfo type = ScriptApiReference.Describe(receiver)
            ?? throw new AutomationException(AutomationErrorCodes.Usage,
                $"Unknown API name '{receiver}'. {ScriptSuggest.Hint(receiver, ScriptApiReference.DescribableNames(), "name")}");
        if (sep < 0)
        {
            return (type.Name, type.Text);
        }

        string memberName = q[(sep + 1)..];
        List<ScriptMemberInfo> matches = type.Members
            .Where(m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            throw new AutomationException(AutomationErrorCodes.Usage,
                $"'{type.Name}' has no member '{memberName}'. {ScriptSuggest.Hint(memberName, type.Members.Select(m => m.Name), "member")}");
        }

        bool global = type.Name == "kit" || ScriptApiReference.GlobalNames().Contains(type.Name, StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (ScriptMemberInfo m in matches)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            string call = type.Name == "kit"
                ? m.Signature
                : $"{type.Name}{(m.Kind == "method" && !global ? ":" : ".")}{m.Signature}";
            sb.Append(call);
            if (type.Name != "kit")
            {
                sb.Append(" -> ").Append(m.Returns);
            }

            sb.AppendLine();
            if (m.Doc.Length > 0)
            {
                sb.Append("  ").AppendLine(m.Doc);
            }

            if (m.Options.Length > 0)
            {
                sb.AppendLine("  Options:");
                foreach (ScriptOptionInfo o in m.Options)
                {
                    sb.Append("    ").Append(o.Name).Append(": ").Append(o.Type);
                    if (o.Default.Length > 0)
                    {
                        sb.Append(" = ").Append(o.Default);
                    }

                    sb.Append(" — ").AppendLine(o.Doc);
                }
            }

            if (m.Example.Length > 0)
            {
                sb.Append("  Example: ").AppendLine(m.Example);
            }
        }

        return ($"{type.Name}.{matches[0].Name}", sb.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// The full Markdown reference as an install prints it: the generator's maintainer note (how the
    /// repository copy is regenerated) does not apply to a printed reference, so it is left out.
    /// </summary>
    public static string Markdown() => string.Join('\n', ScriptApiReference.GenerateMarkdown()
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n')
        .Where(l => !l.StartsWith("Do not edit by hand", StringComparison.Ordinal)));

    /// <summary>
    /// An index of the reference, one line per name: the globals, the handle/value types, and the design kit's
    /// functions, each with its one-line summary. Ask for any name with <see cref="Member"/>.
    /// </summary>
    public static string Index()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Lua scripting reference index (ask for one name: a global, a type, or a member such as level.brush or kit.room).");
        sb.AppendLine();
        IReadOnlyList<string> globals = ScriptApiReference.GlobalNames();
        sb.AppendLine("Globals:");
        foreach (string g in globals)
        {
            Line(sb, g, ScriptApiReference.Describe(g)?.Summary);
        }

        sb.AppendLine();
        sb.AppendLine("Types:");
        foreach (string t in ScriptApiReference.DescribableNames().Where(n => n != "kit" && !globals.Contains(n, StringComparer.Ordinal)))
        {
            Line(sb, t, ScriptApiReference.Describe(t)?.Summary);
        }

        if (ScriptApiReference.Describe("kit") is { } kit)
        {
            sb.AppendLine();
            sb.AppendLine("Design kit (local kit = ged.import(\"kit\")):");
            foreach (ScriptMemberInfo m in kit.Members)
            {
                Line(sb, "kit." + m.Name, m.Doc);
            }
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static void Line(StringBuilder sb, string name, string? doc)
    {
        string first = FirstSentence(doc);
        sb.Append("  ").Append(name);
        if (first.Length > 0)
        {
            sb.Append(" — ").Append(first);
        }

        sb.AppendLine();
    }

    private static string FirstSentence(string? text)
    {
        string t = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        int dot = t.IndexOf(". ", StringComparison.Ordinal);
        string s = dot > 0 ? t[..(dot + 1)] : t;
        return s.Length > 160 ? s[..157] + "..." : s;
    }
}

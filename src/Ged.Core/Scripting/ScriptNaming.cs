using System.Text;

namespace Ged.Core.Scripting;

/// <summary>
/// The single source of truth for how PascalCase C# facade members map to snake_case Lua names.
/// Both the MoonSharp binding (which registers the aliases) and the API-reference generator (which
/// documents them) call this, so the generated docs / Lua stub can never drift from the live surface.
/// </summary>
public static class ScriptNaming
{
    /// <summary>Converts a PascalCase / camelCase identifier to snake_case (Uid→uid, SetPos→set_pos, X→x).</summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool boundary = i > 0 &&
                    (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (boundary)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

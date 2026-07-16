using System;
using System.Text;

namespace Ged.Core.Input;

/// <summary>Modifier keys held for a gesture.</summary>
[Flags]
public enum GestureModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// A keyboard gesture: a normalized key token plus modifier flags, e.g.
/// <c>Ctrl+Shift+P</c>. The key token is a device-independent string ("A", "F4",
/// "Numpad2", "Home", "Plus", "]") so gestures serialize cleanly and are compared
/// case-insensitively on the key. The App translates platform key events into
/// this type before matching against the keymap.
/// </summary>
public readonly record struct KeyGesture(string Key, GestureModifiers Modifiers = GestureModifiers.None)
{
    /// <summary>The normalized key with no modifiers stripped/added.</summary>
    public string Key { get; } = NormalizeKey(Key);

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    public bool Equals(KeyGesture other) =>
        Modifiers == other.Modifiers && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(Key.ToUpperInvariant(), Modifiers);

    /// <summary>Parses a gesture string like "Ctrl+Shift+P"; throws on malformed input.</summary>
    public static KeyGesture Parse(string text) =>
        TryParse(text, out KeyGesture g) ? g : throw new FormatException($"Invalid gesture '{text}'.");

    public static bool TryParse(string? text, out KeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        GestureModifiers mods = GestureModifiers.None;
        string? key = null;
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.Length == 0)
            {
                continue;
            }

            bool isLast = i == parts.Length - 1;
            switch (p.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL" when !isLast:
                    mods |= GestureModifiers.Ctrl;
                    break;
                case "SHIFT" when !isLast:
                    mods |= GestureModifiers.Shift;
                    break;
                case "ALT" when !isLast:
                    mods |= GestureModifiers.Alt;
                    break;
                default:
                    key = p;
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        gesture = new KeyGesture(key, mods);
        return true;
    }

    /// <summary>Canonical serialized form (e.g. "Ctrl+Shift+P").</summary>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        if ((Modifiers & GestureModifiers.Ctrl) != 0)
        {
            sb.Append("Ctrl+");
        }

        if ((Modifiers & GestureModifiers.Shift) != 0)
        {
            sb.Append("Shift+");
        }

        if ((Modifiers & GestureModifiers.Alt) != 0)
        {
            sb.Append("Alt+");
        }

        sb.Append(Key);
        return sb.ToString();
    }

    /// <summary>A friendlier label for menus/tooltips (maps Plus/Minus to symbols).</summary>
    public string Display => ToString()
        .Replace("Plus", "+", StringComparison.Ordinal)
        .Replace("Minus", "-", StringComparison.Ordinal);

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        key = key.Trim();

        // Single letters are upper-cased; longer tokens keep their casing.
        return key.Length == 1 && char.IsLetter(key[0]) ? key.ToUpperInvariant() : key;
    }
}

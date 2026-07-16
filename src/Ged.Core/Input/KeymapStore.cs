using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ged.Core.Input;

/// <summary>
/// Loads and saves a <see cref="Keymap"/> as JSON in the portable keymap file
/// <c>keymap.cfg</c> (see <see cref="AppPaths"/>): next to the executable when its
/// directory is writable, otherwise under <c>%APPDATA%\Glacier</c>. Only the
/// preset name and the user overrides are persisted; the preset base is reconstructed
/// from <see cref="CommandCatalog"/> at load. An override value of null means the
/// command was explicitly unbound.
/// </summary>
public static class KeymapStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The active keymap file path (portable, or the profile fallback).</summary>
    public static string KeymapPath => AppPaths.KeymapFile;

    /// <summary>The on-disk shape (kept minimal and stable).</summary>
    public sealed class KeymapData
    {
        public string PresetName { get; set; } = CommandCatalog.RedClassic;

        /// <summary>Command id → gesture string; a null value means "unbound".</summary>
        public Dictionary<string, string?> Overrides { get; set; } = new();
    }

    public static string Serialize(Keymap keymap)
    {
        var data = new KeymapData { PresetName = keymap.PresetName };
        foreach (var kv in keymap.Overrides)
        {
            data.Overrides[kv.Key] = kv.Value?.ToString();
        }

        return JsonSerializer.Serialize(data, Options);
    }

    public static Keymap Deserialize(string json)
    {
        KeymapData data = JsonSerializer.Deserialize<KeymapData>(json) ?? new KeymapData();
        return FromData(data);
    }

    public static Keymap FromData(KeymapData data)
    {
        string preset = CommandCatalog.PresetNames.Contains(data.PresetName)
            ? data.PresetName
            : CommandCatalog.RedClassic;
        var keymap = Keymap.FromPreset(preset);
        foreach (var kv in data.Overrides)
        {
            if (kv.Value is null)
            {
                keymap.Rebind(kv.Key, null);
            }
            else if (KeyGesture.TryParse(kv.Value, out KeyGesture g))
            {
                keymap.Rebind(kv.Key, g);
            }
        }

        return keymap;
    }

    /// <summary>Loads the persisted keymap, or a fresh RED Classic keymap on any failure.</summary>
    public static Keymap Load()
    {
        try
        {
            if (File.Exists(KeymapPath))
            {
                return Deserialize(File.ReadAllText(KeymapPath));
            }
        }
        catch (Exception)
        {
            // Fall through to default.
        }

        return Keymap.FromPreset(CommandCatalog.RedClassic);
    }

    public static void Save(Keymap keymap)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeymapPath)!);
            File.WriteAllText(KeymapPath, Serialize(keymap));
        }
        catch (Exception)
        {
            // Non-fatal.
        }
    }
}

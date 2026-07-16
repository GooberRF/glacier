using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ged.Core.Tables;

/// <summary>
/// A single glare (corona) class from <c>effects.tbl</c>'s <c>#Glares</c> section. Field
/// defaults mirror Alpine's <c>GlareClassInfo</c> (editor_patch/tbl.h:203-215); the object→mesh
/// converter maps these onto a spawned <c>AlpineCoronaObject</c> (via <c>create_corona_from_glare</c>,
/// editor_patch/corona.cpp:638-666).
/// </summary>
public sealed class GlareDef
{
    public GlareDef(TblRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        Name = record.GetString("Name") ?? string.Empty;
        (ColorR, ColorG, ColorB) = ParseColor(record.GetRaw("Light Color"));
        CoronaBitmap = record.GetString("Corona Bitmap") ?? string.Empty;
        ConeAngle = record.GetFloat("Cone Angle") ?? 90f;
        Intensity = record.GetFloat("Intensity") ?? 1f;
        RadiusDistance = record.GetFloat("Radius Distance Factor") ?? 0.6f;
        RadiusScale = record.GetFloat("Radius Scale Factor") ?? 1f;
        DiminishDistance = record.GetFloat("Diminish Distance") ?? -0.05f;
        VolumetricBitmap = record.GetString("Volumetric Bitmap") ?? string.Empty;
        VolumetricHeight = record.GetFloat("Volumetric Height") ?? 0f;
        VolumetricLength = record.GetFloat("Volumetric Length") ?? 0f;
    }

    public string Name { get; }

    public byte ColorR { get; }

    public byte ColorG { get; }

    public byte ColorB { get; }

    public string CoronaBitmap { get; }

    public float ConeAngle { get; }

    public float Intensity { get; }

    public float RadiusDistance { get; }

    public float RadiusScale { get; }

    public float DiminishDistance { get; }

    public string VolumetricBitmap { get; }

    public float VolumetricHeight { get; }

    public float VolumetricLength { get; }

    public TblRecord Record { get; }

    public override string ToString() => Name;

    // "$Light Color: {R, G, B}" — commas are not whitespace, so parse the braced list by hand.
    private static (byte, byte, byte) ParseColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (255, 255, 255);
        }

        string body = raw.Trim().Trim('{', '}');
        byte[] c = { 255, 255, 255 };
        string[] parts = body.Split(',');
        for (int i = 0; i < 3 && i < parts.Length; i++)
        {
            if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            {
                c[i] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        return (c[0], c[1], c[2]);
    }
}

/// <summary>Typed catalog of <c>effects.tbl</c> <c>#Glares</c>, indexed by name (case-insensitive).</summary>
public sealed class GlareCatalog
{
    private readonly Dictionary<string, GlareDef> _byName;

    private GlareCatalog(IReadOnlyList<GlareDef> glares)
    {
        Glares = glares;
        _byName = new Dictionary<string, GlareDef>(StringComparer.OrdinalIgnoreCase);
        foreach (GlareDef g in glares)
        {
            _byName.TryAdd(g.Name, g);
        }
    }

    public IReadOnlyList<GlareDef> Glares { get; }

    public static GlareCatalog Load(byte[] data) => Parse(TblParser.Parse(data));

    public static GlareCatalog Load(string text) => Parse(TblParser.Parse(text));

    public static GlareCatalog Parse(TblDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var glares = doc.InSection("Glares")
            .Where(r => r.Has("Name") && !string.IsNullOrEmpty(r.GetString("Name")))
            .Select(r => new GlareDef(r))
            .ToList();
        return new GlareCatalog(glares);
    }

    public GlareDef? Find(string glareName) =>
        glareName is not null && _byName.TryGetValue(glareName, out GlareDef? d) ? d : null;
}

using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Reads and edits the GED-only <c>ged_object_metadata</c> chunk (0x6ED00002, item 4) over an
/// <see cref="EditorDocument"/>: a general, extensible per-object metadata container keyed by UID.
/// The first user is light projection cookies (<see cref="GedMetadataType.LightCookie"/>).
/// <para>All edits are undoable and byte-identity-safe: the chunk is created lazily on first real
/// use and REMOVED again when it holds no entries, so a level that never carried metadata — or one
/// whose only metadata was set then cleared — saves byte-identically. Unknown block types are never
/// touched, so they round-trip opaquely.</para>
/// </summary>
public sealed class GedObjectMetadataService
{
    private readonly EditorDocument _doc;

    public GedObjectMetadataService(EditorDocument doc) => _doc = doc ?? throw new ArgumentNullException(nameof(doc));

    /// <summary>Raised after any metadata block is added / changed / removed.</summary>
    public event Action? MetadataChanged;

    // ---- reads ---------------------------------------------------------------

    /// <summary>The metadata entry for <paramref name="uid"/>, or null.</summary>
    public GedObjectMetadataRecord? Entry(int uid) => FindSection()?.Entries.FirstOrDefault(e => e.Uid == uid);

    /// <summary>The raw payload of the first block of <paramref name="type"/> on <paramref name="uid"/>, or null.</summary>
    public byte[]? BlockPayload(int uid, GedMetadataType type) =>
        Entry(uid)?.Blocks.FirstOrDefault(b => b.MetadataType == (uint)type)?.Payload;

    /// <summary>Default cookie sharpness (item 6): 1.0 = crisp (raw sample), 0.0 = fully blurred.</summary>
    public const float DefaultSharpness = 1f;

    /// <summary>The projection-cookie filename set on the light <paramref name="uid"/>, or null.</summary>
    public string? Cookie(int uid)
    {
        byte[]? payload = BlockPayload(uid, GedMetadataType.LightCookie);
        return payload is null ? null : DecodeCookie(payload).File;
    }

    /// <summary>The projection-cookie sharpness set on the light <paramref name="uid"/> (item 6); 1.0 when none / no cookie.</summary>
    public float CookieSharpness(int uid)
    {
        byte[]? payload = BlockPayload(uid, GedMetadataType.LightCookie);
        return payload is null ? DefaultSharpness : DecodeCookie(payload).Sharpness;
    }

    /// <summary>Every light-cookie mapping (light UID → cookie filename) for the baker.</summary>
    public IReadOnlyDictionary<int, string> AllCookies()
    {
        var map = new Dictionary<int, string>();
        if (FindSection() is { } section)
        {
            foreach (GedObjectMetadataRecord rec in section.Entries)
            {
                GedObjectMetadataBlock? cookie = rec.Blocks.FirstOrDefault(b => b.MetadataType == (uint)GedMetadataType.LightCookie);
                if (cookie is not null)
                {
                    map[rec.Uid] = DecodeCookie(cookie.Payload).File;
                }
            }
        }

        return map;
    }

    /// <summary>Every light-cookie sharpness (light UID → sharpness) for the baker (item 6).</summary>
    public IReadOnlyDictionary<int, float> AllCookieSharpness()
    {
        var map = new Dictionary<int, float>();
        if (FindSection() is { } section)
        {
            foreach (GedObjectMetadataRecord rec in section.Entries)
            {
                GedObjectMetadataBlock? cookie = rec.Blocks.FirstOrDefault(b => b.MetadataType == (uint)GedMetadataType.LightCookie);
                if (cookie is not null)
                {
                    map[rec.Uid] = DecodeCookie(cookie.Payload).Sharpness;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Decodes a LightCookie payload. New payloads are <c>vstring filename + f32 sharpness</c>;
    /// the reader is tolerant of the old filename-only payload — if no bytes remain after the
    /// vstring, sharpness defaults to 1.0 (item 6).
    /// </summary>
    private static (string File, float Sharpness) DecodeCookie(byte[] payload)
    {
        var r = new RfReader(payload);
        string file = r.ReadVString();
        float sharpness = r.Remaining >= 4 ? r.ReadF32() : DefaultSharpness;
        return (file, sharpness);
    }

    // ---- writes (undoable, byte-identity-safe) -------------------------------

    /// <summary>
    /// Sets (or clears, when <paramref name="filename"/> is null/blank) the projection cookie for a
    /// light, with an optional sharpness (item 6; 1.0 = crisp). The payload is
    /// <c>vstring filename + f32 sharpness</c>.
    /// </summary>
    public void SetCookie(int uid, string? filename, float sharpness = DefaultSharpness)
    {
        byte[]? payload = string.IsNullOrWhiteSpace(filename) ? null : EncodeCookie(filename!, sharpness);
        SetBlock(uid, GedMetadataType.LightCookie, payload,
            payload is null ? "Clear light projection cookie" : "Set light projection cookie");
    }

    /// <summary>
    /// Updates only the sharpness of an existing cookie (item 6), preserving its filename, as one
    /// undoable step. No-op when the light has no cookie set.
    /// </summary>
    public void SetCookieSharpness(int uid, float sharpness)
    {
        byte[]? payload = BlockPayload(uid, GedMetadataType.LightCookie);
        if (payload is null)
        {
            return; // no cookie to sharpen
        }

        (string file, _) = DecodeCookie(payload);
        SetBlock(uid, GedMetadataType.LightCookie, EncodeCookie(file, sharpness), "Set cookie sharpness");
    }

    /// <summary>
    /// Adds/replaces (payload != null) or removes (payload == null) the <paramref name="type"/> block on
    /// <paramref name="uid"/> as one undoable step. An entry with no blocks is dropped, and the whole
    /// chunk is dropped when it holds no entries (byte-identity).
    /// </summary>
    public void SetBlock(int uid, GedMetadataType type, byte[]? payload, string? description = null)
    {
        List<GedObjectMetadataRecord> before = Snapshot();
        List<GedObjectMetadataRecord> after = before.Select(e => e.Clone()).ToList();

        GedObjectMetadataRecord? rec = after.FirstOrDefault(e => e.Uid == uid);
        if (rec is null)
        {
            if (payload is null)
            {
                return; // removing an absent block: nothing to do
            }

            rec = new GedObjectMetadataRecord { Uid = uid };
            after.Add(rec);
        }

        rec.Blocks.RemoveAll(b => b.MetadataType == (uint)type);
        if (payload is not null)
        {
            rec.Blocks.Add(new GedObjectMetadataBlock(type, payload));
        }

        if (rec.Blocks.Count == 0)
        {
            after.Remove(rec);
        }

        if (Serialize(before).AsSpan().SequenceEqual(Serialize(after)))
        {
            return; // no observable change
        }

        _doc.Undo.Execute(new RelayCommand(description ?? "Edit object metadata",
            () => Apply(after),
            () => Apply(before)));
    }

    // ---- plumbing ------------------------------------------------------------

    private void Apply(List<GedObjectMetadataRecord> entries)
    {
        if (entries.Count == 0)
        {
            RemoveSection();
        }
        else
        {
            RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.GedObjectMetadata, () => new GedObjectMetadataSection());
            EnsurePresent(host);
            ((GedObjectMetadataSection)host.Content!).Entries = entries.Select(e => e.Clone()).ToList();
            host.Dirty = true;
        }

        MetadataChanged?.Invoke();
    }

    private List<GedObjectMetadataRecord> Snapshot() =>
        FindSection()?.Entries.Select(e => e.Clone()).ToList() ?? new List<GedObjectMetadataRecord>();

    private static byte[] Serialize(List<GedObjectMetadataRecord> entries)
    {
        var writer = new RfWriter(64);
        new GedObjectMetadataSection { Entries = entries }.Write(writer, new RflContext(0));
        return writer.ToArray();
    }

    private static byte[] EncodeCookie(string filename, float sharpness)
    {
        var w = new RfWriter(filename.Length + 6);
        w.WriteVString(filename);
        w.WriteF32(sharpness);
        return w.ToArray();
    }

    private GedObjectMetadataSection? FindSection()
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is GedObjectMetadataSection g)
            {
                return g;
            }
        }

        return null;
    }

    private void RemoveSection()
    {
        for (int i = 0; i < _doc.Rfl.Sections.Count; i++)
        {
            if (_doc.Rfl.Sections[i].Content is GedObjectMetadataSection)
            {
                _doc.Rfl.Sections.RemoveAt(i);
                return;
            }
        }
    }

    private void EnsurePresent(RflSection host)
    {
        if (_doc.Rfl.Sections.Contains(host))
        {
            return;
        }

        _doc.Rfl.InsertSection(host);
    }
}

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
    public GedObjectMetadataRecord? Entry(int uid)
    {
        GedObjectMetadataSection? s = FindSection();
        if (s is null)
        {
            return null;
        }

        int at = IndexOf(s, uid);
        return at >= 0 ? s.Entries[at] : null;
    }

    /// <summary>The position of <paramref name="uid"/>'s first record in the section, or -1.</summary>
    private int IndexOf(GedObjectMetadataSection s, int uid)
    {
        // A uid -> position index (a kit run reads and writes the tags of thousands of brushes, and a scan per call
        // made each cost the whole chunk): rebuilt when the list, its length or its revision moved; a hit is checked
        // against the list itself.
        List<GedObjectMetadataRecord> list = s.Entries;
        if (!ReferenceEquals(list, _indexList) || list.Count != _indexCount || s.Revision != _indexRevision)
        {
            _index.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                _index.TryAdd(list[i].Uid, i);
            }

            _indexList = list;
            _indexCount = list.Count;
            _indexRevision = s.Revision;
        }

        if (!_index.TryGetValue(uid, out int at))
        {
            return -1;
        }

        return at < list.Count && list[at].Uid == uid ? at : list.FindIndex(e => e.Uid == uid);
    }

    private List<GedObjectMetadataRecord>? _indexList;
    private int _indexCount = -1;
    private int _indexRevision = -1;
    private readonly Dictionary<int, int> _index = new();

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
    public void SetBlock(int uid, GedMetadataType type, byte[]? payload, string? description = null) =>
        SetBlocks(new[] { (uid, type, payload) }, description ?? "Edit object metadata");

    /// <summary>
    /// Applies a batch of block writes — each adds/replaces (payload != null) or removes (payload ==
    /// null) the <c>Type</c> block on <c>Uid</c> — as ONE undoable step with a single snapshot and
    /// serialization (auto-tagging thousands of UIDs stays linear). Entries left with no blocks are
    /// dropped, and the chunk is dropped when it holds no entries (byte-identity); new entries are
    /// appended in batch order. A batch with no observable change records nothing. The chunk is
    /// created, and removed again on undo, inside the command, and an existing chunk keeps its
    /// position in the file.
    /// </summary>
    public void SetBlocks(IReadOnlyList<(int Uid, GedMetadataType Type, byte[]? Payload)> writes, string description)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            return;
        }

        // Only the records the batch touches are copied and compared (a kit run tags thousands of UIDs one call at a
        // time, so copying and serializing the whole chunk per write would be quadratic). The first record of a UID
        // is the one written, and the resulting entry order is the same as rewriting the whole list.
        RflSection? existing = FindHost();
        var existingSection = existing?.Content as GedObjectMetadataSection;
        List<GedObjectMetadataRecord> entries = existingSection?.Entries ?? new List<GedObjectMetadataRecord>();

        var changes = new Dictionary<int, (int Index, GedObjectMetadataRecord? Before, GedObjectMetadataRecord After)>();
        var order = new List<int>();
        foreach ((int uid, GedMetadataType type, byte[]? payload) in writes)
        {
            if (!changes.TryGetValue(uid, out var c))
            {
                int idx = existingSection is null ? -1 : IndexOf(existingSection, uid);
                GedObjectMetadataRecord? before = idx >= 0 ? entries[idx].Clone() : null;
                c = (idx, before, before?.Clone() ?? new GedObjectMetadataRecord { Uid = uid });
                changes[uid] = c;
                order.Add(uid);
            }

            c.After.Blocks.RemoveAll(b => b.MetadataType == (uint)type);
            if (payload is not null)
            {
                c.After.Blocks.Add(new GedObjectMetadataBlock(type, payload));
            }
        }

        var changed = order.Select(uid => changes[uid]).Where(c =>
        {
            var b = c.Before is null ? new List<GedObjectMetadataRecord>() : new List<GedObjectMetadataRecord> { c.Before };
            var a = c.After.Blocks.Count == 0 ? new List<GedObjectMetadataRecord>() : new List<GedObjectMetadataRecord> { c.After };
            return !Serialize(b).AsSpan().SequenceEqual(Serialize(a));
        }).ToList();
        if (changed.Count == 0)
        {
            return; // no observable change
        }

        // The host is resolved once, so do and undo act on the same section object: an existing chunk is restored at
        // its original index, a new one is inserted at its canonical position.
        int existingIndex = existing is null ? -1 : _doc.Rfl.Sections.IndexOf(existing);
        bool wasDirty = existing?.Dirty ?? true;
        RflSection host = existing ?? new RflSection((uint)SectionType.GedObjectMetadata, Array.Empty<byte>())
        {
            Content = new GedObjectMetadataSection(),
            Dirty = true,
        };
        var replaced = changed.Where(c => c.Index >= 0 && c.After.Blocks.Count > 0).ToList();
        var removed = changed.Where(c => c.Index >= 0 && c.After.Blocks.Count == 0).OrderBy(c => c.Index).ToList();
        var added = changed.Where(c => c.Index < 0 && c.After.Blocks.Count > 0).Select(c => c.After).ToList();

        void Do()
        {
            ((GedObjectMetadataSection)host.Content!).Revision++;
            List<GedObjectMetadataRecord> list = ((GedObjectMetadataSection)host.Content!).Entries;
            foreach (var c in replaced)
            {
                list[c.Index] = c.After.Clone();
            }

            for (int i = removed.Count - 1; i >= 0; i--)
            {
                list.RemoveAt(removed[i].Index);
            }

            foreach (GedObjectMetadataRecord r in added)
            {
                list.Add(r.Clone());
            }

            Place(host, existingIndex, list.Count > 0, dirty: true);
        }

        void Undo()
        {
            ((GedObjectMetadataSection)host.Content!).Revision++;
            List<GedObjectMetadataRecord> list = ((GedObjectMetadataSection)host.Content!).Entries;
            // Undo runs on the state this command left (later commands undone first): its new records are the last ones.
            list.RemoveRange(list.Count - added.Count, added.Count);

            foreach (var c in removed)
            {
                list.Insert(c.Index, c.Before!.Clone());
            }

            foreach (var c in replaced)
            {
                list[c.Index] = c.Before!.Clone();
            }

            Place(host, existingIndex, list.Count > 0, dirty: wasDirty);
        }

        _doc.Undo.Execute(new RelayCommand(description, Do, Undo));
    }

    /// <summary>Puts the chunk in the file while it holds entries and drops it when it holds none (byte-identity).</summary>
    private void Place(RflSection host, int existingIndex, bool hasEntries, bool dirty)
    {
        if (!hasEntries)
        {
            _doc.Rfl.Sections.Remove(host);
        }
        else
        {
            if (!_doc.Rfl.Sections.Contains(host))
            {
                if (existingIndex >= 0)
                {
                    _doc.Rfl.Sections.Insert(Math.Clamp(existingIndex, 0, _doc.Rfl.Sections.Count), host);
                }
                else
                {
                    _doc.Rfl.InsertSection(host);
                }
            }

            host.Dirty = dirty;
        }

        MetadataChanged?.Invoke();
    }

    // ---- tags ------------------------------------------------------

    /// <summary>The provenance tags on <paramref name="uid"/> (empty when none or malformed).</summary>
    public Dictionary<string, string> Tags(int uid) => ObjectTags.Decode(BlockPayload(uid, GedMetadataType.Tags));

    /// <summary>Every UID's tags, decoded once (UIDs without a tag block are absent).</summary>
    public IReadOnlyDictionary<int, Dictionary<string, string>> AllTags()
    {
        var map = new Dictionary<int, Dictionary<string, string>>();
        if (FindSection() is { } section)
        {
            foreach (GedObjectMetadataRecord rec in section.Entries)
            {
                GedObjectMetadataBlock? block = rec.Blocks.FirstOrDefault(b => b.MetadataType == (uint)GedMetadataType.Tags);
                if (block is not null && !map.ContainsKey(rec.Uid))
                {
                    map[rec.Uid] = ObjectTags.Decode(block.Payload);
                }
            }
        }

        return map;
    }

    // ---- delete-time tag cleanup ----------------------------------------------

    /// <summary>What <see cref="DropTagsOfDeleted"/> changed, so the owning delete command can undo it.</summary>
    internal sealed class TagDropSnapshot
    {
        internal TagDropSnapshot(RflSection host, int index, List<GedObjectMetadataRecord> entries, bool dirty)
        {
            Host = host;
            Index = index;
            Entries = entries;
            Dirty = dirty;
        }

        public RflSection Host { get; }

        public int Index { get; }

        public List<GedObjectMetadataRecord> Entries { get; }

        public bool Dirty { get; }
    }

    /// <summary>
    /// Removes the Tags block of every UID in <paramref name="deleted"/> that no longer exists
    /// (<paramref name="isLive"/> false), dropping entries left empty and the chunk itself when nothing
    /// remains. Called INSIDE a delete command's do-action so the delete and the tag cleanup are one
    /// undo step; <see cref="RestoreDroppedTags"/> reverts it. Other block types (a light's cookie) are
    /// kept. Returns null — and changes nothing — when none of the deleted UIDs carried tags, so a
    /// level without tags is untouched byte for byte.
    /// </summary>
    internal static TagDropSnapshot? DropTagsOfDeleted(RflFile rfl, IEnumerable<int> deleted, Func<int, bool> isLive)
    {
        rfl.ParseAllKnownSections();
        RflSection? host = rfl.Sections.FirstOrDefault(s => s.Content is GedObjectMetadataSection);
        if (host?.Content is not GedObjectMetadataSection section)
        {
            return null;
        }

        var dead = deleted.Where(u => !isLive(u)).ToHashSet();
        const uint TagsType = (uint)GedMetadataType.Tags;
        if (dead.Count == 0 || !section.Entries.Any(e => dead.Contains(e.Uid) && e.Blocks.Any(b => b.MetadataType == TagsType)))
        {
            return null;
        }

        var snapshot = new TagDropSnapshot(host, rfl.Sections.IndexOf(host), section.Entries, host.Dirty);
        var after = new List<GedObjectMetadataRecord>(section.Entries.Count);
        foreach (GedObjectMetadataRecord rec in section.Entries)
        {
            if (!dead.Contains(rec.Uid))
            {
                after.Add(rec);
                continue;
            }

            GedObjectMetadataRecord kept = rec.Clone();
            kept.Blocks.RemoveAll(b => b.MetadataType == TagsType);
            if (kept.Blocks.Count > 0)
            {
                after.Add(kept);
            }
        }

        if (after.Count == 0)
        {
            rfl.Sections.Remove(host);
        }
        else
        {
            section.Entries = after;
            host.Dirty = true;
        }

        return snapshot;
    }

    /// <summary>Undoes <see cref="DropTagsOfDeleted"/>: the chunk returns at its old index with its old entries and dirty state.</summary>
    internal static void RestoreDroppedTags(RflFile rfl, TagDropSnapshot snapshot)
    {
        if (!rfl.Sections.Contains(snapshot.Host))
        {
            rfl.Sections.Insert(Math.Clamp(snapshot.Index, 0, rfl.Sections.Count), snapshot.Host);
        }

        ((GedObjectMetadataSection)snapshot.Host.Content!).Entries = snapshot.Entries;
        snapshot.Host.Dirty = snapshot.Dirty;
    }

    // ---- plumbing ------------------------------------------------------------

    /// <summary>Installs <paramref name="entries"/> (or removes the chunk when empty); undo restores the chunk's prior dirty state so an untouched loaded chunk saves from its original bytes.</summary>
    private void Apply(RflSection host, int existingIndex, List<GedObjectMetadataRecord> entries, bool dirty)
    {
        if (entries.Count == 0)
        {
            _doc.Rfl.Sections.Remove(host);
        }
        else
        {
            if (!_doc.Rfl.Sections.Contains(host))
            {
                if (existingIndex >= 0)
                {
                    _doc.Rfl.Sections.Insert(Math.Clamp(existingIndex, 0, _doc.Rfl.Sections.Count), host);
                }
                else
                {
                    _doc.Rfl.InsertSection(host);
                }
            }

            ((GedObjectMetadataSection)host.Content!).Entries = entries.Select(e => e.Clone()).ToList();
            host.Dirty = dirty;
        }

        MetadataChanged?.Invoke();
    }

    private RflSection? FindHost()
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is GedObjectMetadataSection)
            {
                return s;
            }
        }

        return null;
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
}

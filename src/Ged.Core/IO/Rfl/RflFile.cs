using System.Text;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.IO.Rfl;

/// <summary>
/// An RFL level file: a header plus an ordered list of sections. Load captures
/// every section's raw bytes so that an unmodified <see cref="Save"/> is
/// byte-identical to the input (modulo an optional timestamp update). Header
/// offsets and counts are recomputed from the section layout on every save.
///
/// <para>
/// GED's format policy is Alpine-only output: a level is written to disk through
/// <see cref="UpgradeToAlpine"/> + <see cref="Save(bool)"/>, which always produces
/// a v306 file (matches Alpine RED, which writes MAXIMUM_RFL_VERSION = 306 on every
/// save — editor_patch/main.cpp:2295-2302, LoadSaveLevel_patch @ 0x0041CD20;
/// common/include/common/version/version.h:52). Loading accepts every version GED
/// understands (0xB4/180, 0xC8/200, and Alpine 300–306); a newer file opens
/// read-only and cannot be saved (<see cref="IsSaveBlocked"/>), as Alpine RED refuses
/// it (main.cpp:2352-2364). <see cref="Save(bool)"/> itself is a faithful serializer of
/// whatever version the header currently carries, so a v306 source stays
/// byte-identical on a no-op save.
/// </para>
/// </summary>
public sealed class RflFile
{
    /// <summary>Fixed portion of the header, magic through sections_total_size.</summary>
    private const int FixedHeaderSize = 28;

    /// <summary>
    /// The RFL version GED writes for every level: Alpine v306 (0x132), the current
    /// Alpine Faction level version (MAXIMUM_RFL_VERSION, Alpine 1.5). GED never emits a
    /// lower version; <see cref="UpgradeToAlpine"/> retargets any loaded pre-306 level here.
    /// </summary>
    public const int AlpineSaveVersion = 0x132;

    /// <summary>The RFL version Glacier writes (same as <see cref="AlpineSaveVersion"/>).</summary>
    public const int CurrentVersion = AlpineSaveVersion;

    public RflHeader Header { get; set; } = new();

    /// <summary>All sections in file order, including the trailing End terminator when present.</summary>
    public List<RflSection> Sections { get; } = new();

    /// <summary>Any bytes after the final section (normally empty).</summary>
    public byte[] TrailingBytes { get; set; } = Array.Empty<byte>();

    public RflContext Context => new(Header.Version);

    /// <summary>
    /// What Glacier has recomputed in this level since it was loaded: set by every geometry build applied to it and
    /// every relight (<see cref="NoteGlacierLightingChange"/>). A save then drops the level's Alpine lightmap chunk,
    /// which no longer matches (<see cref="ReconcileAlpineLightmaps"/>); <see cref="GlacierLightingChange.None"/>
    /// leaves the file as loaded.
    /// </summary>
    public GlacierLightingChange GlacierLightingChanges { get; private set; }

    /// <summary>
    /// Records that Glacier rebuilt the geometry and/or recalculated the lighting of this level. Any Alpine charts a
    /// previous bake attached no longer match and are discarded; the operation attaches its own with
    /// <see cref="AttachAlpineLightmaps"/> when it baked them.
    /// </summary>
    public void NoteGlacierLightingChange(GlacierLightingChange change)
    {
        GlacierLightingChanges |= change;
        AlpineLightmaps = null;
    }

    /// <summary>
    /// The Alpine lightmaps (RFL chunk 0x0AFBAE09) Glacier baked for this level's current geometry and lightmaps, or
    /// null. A save writes them as the level's Alpine lightmap chunk while they still match what is saved
    /// (<see cref="ReconcileAlpineLightmaps"/>); any later rebuild or relight discards them.
    /// </summary>
    public Ged.Core.Lighting.Alpine.AlpineLightmapBake? AlpineLightmaps { get; private set; }

    /// <summary>Attaches the Alpine charts the bake just applied to this level produced (null for none).</summary>
    public void AttachAlpineLightmaps(Ged.Core.Lighting.Alpine.AlpineLightmapBake? bake) => AlpineLightmaps = bake;

    /// <summary>
    /// Applies the Alpine lightmap save rule (<see cref="AlpineLightmapSavePolicy"/>): once Glacier has rebuilt the
    /// geometry or relit the level, the Alpine lightmap chunk (0x0AFBAE09) is dropped and the props bits that describe
    /// it are made consistent with the stock lightmaps Glacier wrote. Returns the save-log lines (empty when nothing
    /// changed). Every save path runs it (<see cref="UpgradeToAlpine"/>); packaging runs it before serializing.
    /// </summary>
    public IReadOnlyList<string> ReconcileAlpineLightmaps() => AlpineLightmapSavePolicy.Reconcile(this);

    /// <summary>
    /// Set by <see cref="Load(byte[])"/> when the section table could not be read to a clean end: a
    /// section header claimed more bytes than the file holds, or the table has no End terminator.
    /// Informational only — loading behaves exactly as before.
    /// </summary>
    public bool SectionTableIncomplete { get; private set; }

    /// <summary>True for the versions Glacier reads: Volition 180–200 and Alpine 300–306.</summary>
    public static bool IsReadableVersion(int version) =>
        version is >= 0xB4 and <= 0xC8 or >= 0x12C and <= AlpineSaveVersion;

    /// <summary>
    /// True for a level written by a newer Alpine Faction than Glacier knows (RFL version above
    /// <see cref="AlpineSaveVersion"/>). Such a level opens read-only: its newer content cannot be
    /// written back, so every save path refuses it (ALPINE-COMPAT-SPEC §2, Q4).
    /// </summary>
    public static bool IsNewerThanSupported(int version) => version > AlpineSaveVersion;

    /// <summary>True when this file cannot be saved (see <see cref="IsNewerThanSupported"/>).</summary>
    public bool IsSaveBlocked => IsNewerThanSupported(Header.Version);

    /// <summary>The user-facing reason a save is refused, or null when the file can be saved.</summary>
    public string? SaveBlockedReason => IsSaveBlocked
        ? $"This level was saved by a newer version of Alpine Faction (RFL version {Header.Version}); Glacier writes " +
          $"version {AlpineSaveVersion} and cannot save it without losing the newer content. It is open read-only."
        : null;

    public static RflFile Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var reader = new RfReader(data);

        uint magic = reader.ReadU32();
        if (magic != RflHeader.Magic)
        {
            throw new RflFormatException(
                $"Not an RFL file: magic 0x{magic:X8} (expected 0x{RflHeader.Magic:X8}).");
        }

        var file = new RflFile();
        RflHeader header = file.Header;
        header.Version = reader.ReadI32();
        header.Timestamp = reader.ReadU32();
        header.PlayerStartOffset = reader.ReadI32();
        header.LevelInfoOffset = reader.ReadI32();
        header.NumSections = reader.ReadI32();
        header.SectionsTotalSize = reader.ReadI32();
        header.LevelName = reader.ReadVString();

        var context = new RflContext(header.Version);
        header.ModName = context.HasModName ? reader.ReadVString() : null;

        // Sections: {type u32, len s4, body[len]} repeated to EOF. The trailing
        // End section (type 0, len 0) is captured like any other section.
        bool tableBroken = false;
        while (reader.Position + 8 <= data.Length)
        {
            int sectionStart = reader.Position;
            uint typeId = reader.ReadU32();
            int len = reader.ReadI32();
            if (len < 0 || (long)reader.Position + len > data.Length)
            {
                reader.Position = sectionStart;
                tableBroken = true;
                break;
            }

            byte[] body = reader.ReadBytes(len);
            file.Sections.Add(new RflSection(typeId, body));
        }

        file.TrailingBytes = reader.ReadBytes(data.Length - reader.Position);
        file.SectionTableIncomplete = tableBroken || !file.Sections.Exists(s => s.IsEnd);
        return file;
    }

    public static RflFile Load(string path) => Load(File.ReadAllBytes(path));

    /// <summary>
    /// Parses every section for which a parser is registered, attaching the
    /// model to each section. Opaque/unknown sections are left as raw bytes.
    /// </summary>
    public void ParseAllKnownSections()
    {
        RflContext context = Context;
        foreach (RflSection section in Sections)
        {
            if (section.Content is null &&
                RflSectionRegistry.TryParse(section, context, out IRflSectionContent? content))
            {
                section.Content = content;
            }
        }
    }

    /// <summary>
    /// Serializes the file. When no section is dirty and
    /// <paramref name="updateTimestamp"/> is false, the output is byte-identical
    /// to the loaded input.
    /// </summary>
    public byte[] Save(bool updateTimestamp = false)
    {
        RflContext context = Context;

        // A real (dirty) save re-stamps the level_info DATE string to now, in RED's
        // exact format — the same semantics as the header timestamp above: only when
        // updateTimestamp is set, so a clean no-op save stays byte-identical.
        if (updateTimestamp)
        {
            StampDate(context);
        }

        // Materialize every section body first so the layout (and thus the
        // recomputed header offsets) is exact. A D3D11-only Alpine save leaves the stock lightmaps section out and
        // writes every surface record with lightmap index −1 (StockLightmapsSuppressed).
        List<RflSection> sections = StockLightmapsSuppressed
            ? Sections.FindAll(s => s.TypeId != (uint)SectionType.Lightmaps)
            : Sections;
        var bodies = new byte[sections.Count][];
        for (int i = 0; i < sections.Count; i++)
        {
            bodies[i] = StockLightmapsSuppressed ? BodyWithoutLightmapIndices(sections[i], context) : sections[i].GetBodyBytes(context);
        }

        int headerSize = FixedHeaderSize
            + 2 + Encoding.Latin1.GetByteCount(Header.LevelName)
            + (context.HasModName ? 2 + Encoding.Latin1.GetByteCount(Header.ModName ?? string.Empty) : 0);

        // Walk to compute counts, total size, and the player_start / level_info
        // section header offsets.
        int numSections = 0;
        int sectionsTotalSize = 0;
        int playerStartOffset = Header.PlayerStartOffset;
        int levelInfoOffset = Header.LevelInfoOffset;
        int cursor = headerSize;
        for (int i = 0; i < sections.Count; i++)
        {
            RflSection section = sections[i];
            if (!section.IsEnd)
            {
                numSections++;
                sectionsTotalSize += bodies[i].Length;
            }

            if (section.TypeId == (uint)SectionType.PlayerStart)
            {
                playerStartOffset = cursor;
            }
            else if (section.TypeId == (uint)SectionType.LevelInfo)
            {
                levelInfoOffset = cursor;
            }

            cursor += 8 + bodies[i].Length;
        }

        var writer = new RfWriter(cursor + TrailingBytes.Length);
        writer.WriteU32(RflHeader.Magic);
        writer.WriteI32(Header.Version);
        writer.WriteU32(updateTimestamp ? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() : Header.Timestamp);
        writer.WriteI32(playerStartOffset);
        writer.WriteI32(levelInfoOffset);
        writer.WriteI32(numSections);
        writer.WriteI32(sectionsTotalSize);
        writer.WriteVString(Header.LevelName);
        if (context.HasModName)
        {
            writer.WriteVString(Header.ModName ?? string.Empty);
        }

        for (int i = 0; i < sections.Count; i++)
        {
            writer.WriteU32(sections[i].TypeId);
            writer.WriteI32(bodies[i].Length);
            writer.WriteBytes(bodies[i]);
        }

        writer.WriteBytes(TrailingBytes);

        // Keep the in-memory header consistent with what was just written.
        Header.PlayerStartOffset = playerStartOffset;
        Header.LevelInfoOffset = levelInfoOffset;
        Header.NumSections = numSections;
        Header.SectionsTotalSize = sectionsTotalSize;

        return writer.ToArray();
    }

    public void Save(string path, bool updateTimestamp = false) =>
        File.WriteAllBytes(path, Save(updateTimestamp));

    /// <summary>
    /// Set by the Alpine lightmap save rule when the level's fresh Alpine charts stand in for the stock lightmaps
    /// ("D3D11-only lightmaps", <see cref="AlpineLightmapSavePolicy"/>): <see cref="Save(bool)"/> then omits the stock
    /// lightmaps section (0x1200) and writes every static and mover surface record with lightmap index −1, as Alpine RED
    /// does (editor_patch/alpine_lightmaps.cpp:1849-1865, 2015-2049). The document keeps its stock lightmaps in memory.
    /// </summary>
    public bool StockLightmapsSuppressed { get; internal set; }

    /// <summary>
    /// The layer codec this level's next Alpine chart bake writes: BC7 (the default, Alpine RED's shipping codec) or raw
    /// RGB8, the comparison form Alpine RED's <c>-rawlightmaps</c> switch writes (headless_bake.cpp). Not stored in the
    /// level; set by <c>ops.lightmaps</c>, the command line and MCP.
    /// </summary>
    public Ged.Core.Lighting.Alpine.AlpineLightmapCodec AlpineLightmapCodec { get; set; } = Ged.Core.Lighting.Alpine.AlpineLightmapCodec.Bc7;

    /// <summary>A section's body with every surface record's lightmap index written −1 (geometry and movers only).</summary>
    private static byte[] BodyWithoutLightmapIndices(RflSection section, RflContext context)
    {
        List<Geometry>? geometries = section.Content switch
        {
            GeometrySection g when section.TypeId == (uint)SectionType.StaticGeometry => new List<Geometry> { g.Geometry },
            MoversSection m => m.Movers.ConvertAll(b => b.Geometry),
            _ => null,
        };
        if (geometries is null)
        {
            return section.GetBodyBytes(context);
        }

        var saved = new List<(Surface S, int Index)>();
        foreach (Geometry g in geometries)
        {
            foreach (Surface s in g.Surfaces)
            {
                saved.Add((s, s.LightmapIndex));
                s.LightmapIndex = -1;
            }
        }

        bool dirty = section.Dirty;
        try
        {
            section.Dirty = true;
            return section.GetBodyBytes(context);
        }
        finally
        {
            section.Dirty = dirty;
            foreach ((Surface s, int index) in saved)
            {
                s.LightmapIndex = index;
            }
        }
    }

    /// <summary>
    /// Sets the level_info DATE string to the current local date/time in RED's format
    /// (e.g. "Friday, August 24, 2001 16:48:01"). Parses the section on demand so the
    /// date updates on a dirty save even when Level Properties was never opened; marks
    /// it dirty so the new value is re-serialized. No-ops if the level has no level_info.
    /// </summary>
    private void StampDate(RflContext context)
    {
        foreach (RflSection section in Sections)
        {
            if (section.TypeId != (uint)SectionType.LevelInfo)
            {
                continue;
            }

            if (section.Content is null)
            {
                if (!RflSectionRegistry.TryParse(section, context, out IRflSectionContent? parsed))
                {
                    return;
                }

                section.Content = parsed;
            }

            if (section.Content is LevelInfoSection info)
            {
                info.Date = System.DateTime.Now.ToString(
                    LevelInfoSection.DateFormat, System.Globalization.CultureInfo.InvariantCulture);
                section.Dirty = true;
            }

            return;
        }
    }

    /// <summary>
    /// Upgrades the file in place to the Alpine save version (<see cref="AlpineSaveVersion"/>,
    /// v306) and returns the save-log lines the upgrade produced (empty for most saves). This is
    /// the canonical "save = v306 always" step GED applies before writing a level to disk, and it
    /// mirrors Alpine RED, which writes MAXIMUM_RFL_VERSION (306) on every save regardless of the
    /// loaded version (editor_patch/main.cpp:2295-2302).
    ///
    /// <para>
    /// Idempotent: a file already at v306 is left untouched (apart from the props rewrite below), so
    /// a following <see cref="Save(bool)"/> stays byte-identical — the fixpoint invariant (upgrade
    /// once, then re-saving the result is byte-stable). Within the load range the sections whose
    /// on-disk layout is file-version-gated are the embedded <see cref="Geometry"/> (pre-0xC8
    /// modifiability placement and legacy face-scroll table) and, at 306, the Alpine chunks below;
    /// every other section is layout-identical across versions and is emitted verbatim.
    /// </para>
    ///
    /// <para>
    /// A pre-306 source gains exactly what Alpine RED's own load → save of it produces
    /// (ALPINE-COMPAT-SPEC §2): the alpine_level_properties chunk is rewritten at version 6 with
    /// every field (missing ones at Alpine's defaults, and Alpine lighting selected — owner decision
    /// Q6); the Alpine mesh chunk gains its two RFL 306 trailing blocks at their defaults; a non-zero
    /// fog near clip is reset to 0 with one warning (it had no effect before 306 and would now apply —
    /// main.cpp:1529-1543). Play_Sound / Music_Start events whose Looping box is set start looping at
    /// 306 (game_patch/object/event.cpp:498-519); Alpine RED upgrades them silently and so does Glacier,
    /// with one informational line (Q2). No lightmap chunk is synthesised.
    /// </para>
    ///
    /// <para>
    /// On any save, a props chunk in the transient pre-release version 7 layout is rewritten as
    /// version 6 (Q3); its bytes are a version 6 prefix, so nothing is lost. A level Glacier rebuilt or
    /// relit loses its stale Alpine lightmap chunk (<see cref="ReconcileAlpineLightmaps"/>).
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is newer than Glacier writes (<see cref="IsSaveBlocked"/>).</exception>
    public IReadOnlyList<string> UpgradeToAlpine()
    {
        if (SaveBlockedReason is { } blocked)
        {
            throw new InvalidOperationException(blocked);
        }

        var notes = new List<string>();
        int source = Header.Version;
        RewritePreReleaseProperties(notes);
        notes.AddRange(AlpineTerrainSavePolicy.Reconcile(this));
        notes.AddRange(ReconcileAlpineLightmaps());
        if (source == AlpineSaveVersion)
        {
            return notes;
        }

        // Sources at 0xC8..0x131 (community v200, Alpine 300–305) already store
        // geometry in the current layout — only the header version differs. Pre-0xC8
        // sources (Volition v180) embed the old geometry layout and must be re-serialized.
        if (source < 0xC8)
        {
            ParseAllKnownSections();
            foreach (RflSection section in Sections)
            {
                switch (section.Content)
                {
                    case GeometrySection gs:
                        UpgradeGeometry(gs.Geometry);
                        section.Dirty = true;
                        break;
                    case BrushesSection bs:
                        foreach (Brush b in bs.Brushes)
                        {
                            UpgradeGeometry(b.Geometry);
                        }

                        section.Dirty = true;
                        break;
                    case MoversSection ms:
                        foreach (Brush m in ms.Movers)
                        {
                            UpgradeGeometry(m.Geometry);
                        }

                        section.Dirty = true;
                        break;
                }
            }
        }

        // A v300 events section in the orientation-less early-Alpine variant (see
        // EventsSection.OmitsAlpineEventOrientation) is re-serialized at the save version, which
        // writes the AF_Teleport_Player / Clone_Entity matrix the game reads. Every other events
        // section stays verbatim.
        if (source == 0x12C)
        {
            foreach (RflSection section in Sections)
            {
                if (section.TypeId == (uint)SectionType.Events
                    && TryContent(section, new RflContext(source)) is EventsSection { OmitsAlpineEventOrientation: true })
                {
                    section.Dirty = true;
                }
            }
        }

        if (source < 306)
        {
            UpgradeTo306(source, notes);
        }

        Header.Version = AlpineSaveVersion;
        return notes;
    }

    /// <summary>The v306 steps of <see cref="UpgradeToAlpine"/> (see its remarks).</summary>
    private void UpgradeTo306(int source, List<string> notes)
    {
        var context = new RflContext(source);
        int looping = 0;
        foreach (RflSection section in Sections)
        {
            if (section.TypeId is not ((uint)SectionType.AlpineLevelProperties or (uint)SectionType.AlpineMeshObjects
                or (uint)SectionType.LevelProperties or (uint)SectionType.Events))
            {
                continue; // every other section is layout-identical at 306 and stays verbatim
            }

            switch (TryContent(section, context))
            {
                case AlpineLevelPropertiesSection props:
                    props.RewriteAsCurrentLayout();
                    props.LegacyLighting = 0;
                    section.Dirty = true;
                    break;

                case AlpineMeshObjectsSection meshes:
                    meshes.RewriteWithTrailingBlocks();
                    section.Dirty = true;
                    break;

                case LevelPropertiesSection lp when lp.FogNearPlane != 0f:
                    notes.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"Fog near clip {lp.FogNearPlane:0.##} was reset to 0: Red Faction ignored it before RFL 306, and from 306 Alpine Faction applies it on Direct3D 11."));
                    lp.FogNearPlane = 0f;
                    section.Dirty = true;
                    break;

                case EventsSection events:
                    looping += events.Events.Count(e => e.Bool1 != 0 && IsLoopingSoundEvent(e.ClassName));
                    break;
            }
        }

        if (looping > 0)
        {
            notes.Add(looping == 1
                ? "One Play_Sound / Music_Start event has Looping set; from RFL 306 Alpine Faction honours it, so that sound now loops."
                : $"{looping} Play_Sound / Music_Start events have Looping set; from RFL 306 Alpine Faction honours it, so those sounds now loop.");
        }
    }

    /// <summary>Play_Sound and Music_Start: the events whose Looping box (bool1) RFL 306 honours.</summary>
    private static bool IsLoopingSoundEvent(string className) =>
        className.Equals("Play_Sound", StringComparison.OrdinalIgnoreCase)
        || className.Equals("Music_Start", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rewrites a pre-release version 7 props chunk as version 6 (Q3).</summary>
    private void RewritePreReleaseProperties(List<string> notes)
    {
        foreach (RflSection section in Sections)
        {
            if (section.TypeId == (uint)SectionType.AlpineLevelProperties
                && TryContent(section, Context) is AlpineLevelPropertiesSection { Version: > AlpineLevelPropertiesSection.CurrentVersion } props)
            {
                uint was = props.Version;
                props.RewriteAsCurrentLayout();
                section.Dirty = true;
                notes.Add($"The Alpine level properties were stored in a pre-release layout (version {was}) and were rewritten in the current layout (version {AlpineLevelPropertiesSection.CurrentVersion}).");
            }
        }
    }

    /// <summary>The section's parsed model (parsing it on demand), or null when it has no parser or does not parse.</summary>
    private static IRflSectionContent? TryContent(RflSection section, RflContext context)
    {
        if (section.Content is null)
        {
            try
            {
                if (RflSectionRegistry.TryParse(section, context, out IRflSectionContent? parsed))
                {
                    section.Content = parsed;
                }
            }
            catch (Exception)
            {
                return null; // unreadable: emitted verbatim
            }
        }

        return section.Content;
    }

    /// <summary>
    /// Migrates one <see cref="Geometry"/> from a pre-0xC8 (Volition v180) layout to
    /// the v306 layout. Two fields are version-gated: (1) the GeoMod modifiability
    /// value, stored after the name at &lt; 0xC8 (<see cref="Geometry.ModifiabilityOld"/>)
    /// but before the name at &gt;= 0xC8 (<see cref="Geometry.Modifiability"/>) — carry
    /// it across (in the whole example corpus it is always 0, matching the ksy note
    /// "typically zero, unused"); and (2) the legacy face-scroll table (&lt;= 0xB4),
    /// which is gone at v306. Stock v180 levels store their scroll data ONLY in that
    /// legacy table (the modern <c>face_scroll_data</c> table is empty), so it is
    /// moved into the modern table the v306 engine actually reads — without this,
    /// scrolling textures (e.g. dm07's 74 scroll faces) would be lost on upgrade.
    /// </summary>
    private static void UpgradeGeometry(Geometry g)
    {
        if (g.Modifiability == 0)
        {
            g.Modifiability = g.ModifiabilityOld;
        }

        g.ModifiabilityOld = 0;
        g.Unknown1 = 0;

        if (g.LegacyFaceScrollData.Count > 0)
        {
            g.FaceScrollData.AddRange(g.LegacyFaceScrollData);
            g.LegacyFaceScrollData.Clear();
        }
    }

    /// <summary>True for the Alpine/Dash-only section ids that stock RF cannot read.</summary>
    public static bool IsAlpineOnlySection(uint typeId) =>
        SectionIds.IsAlpineSpace(typeId) || typeId == (uint)SectionType.DashLevelProperties;

    /// <summary>
    /// Returns the first section of <paramref name="type"/>, parsing its model if
    /// needed; when absent, creates an empty (dirty) section via
    /// <paramref name="create"/> and inserts it just before the End terminator.
    /// Used by object placement to target any section, even one the level lacks.
    /// </summary>
    public RflSection GetOrCreateSection(SectionType type, Func<IRflSectionContent> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        RflContext context = Context;
        foreach (RflSection s in Sections)
        {
            if (s.TypeId == (uint)type)
            {
                if (s.Content is null &&
                    RflSectionRegistry.TryParse(s, context, out IRflSectionContent? parsed))
                {
                    s.Content = parsed;
                }

                return s;
            }
        }

        var section = new RflSection((uint)type, Array.Empty<byte>()) { Content = create(), Dirty = true };
        InsertSection(section);
        return section;
    }

    /// <summary>
    /// Like <see cref="GetOrCreateSection"/>, but a missing section is returned DETACHED
    /// (<paramref name="created"/> = true): the caller inserts it with <see cref="InsertSection"/>
    /// inside its undo command and removes it again on undo, so undoing the first edit of a section
    /// leaves the file byte-identical.
    /// </summary>
    public RflSection FindOrPrepareSection(SectionType type, Func<IRflSectionContent> create, out bool created)
    {
        ArgumentNullException.ThrowIfNull(create);
        RflContext context = Context;
        foreach (RflSection s in Sections)
        {
            if (s.TypeId == (uint)type)
            {
                if (s.Content is null &&
                    RflSectionRegistry.TryParse(s, context, out IRflSectionContent? parsed))
                {
                    s.Content = parsed;
                }

                created = false;
                return s;
            }
        }

        created = true;
        return new RflSection((uint)type, Array.Empty<byte>()) { Content = create(), Dirty = true };
    }

    /// <summary>
    /// Inserts a NEW section at its canonical position (<see cref="RflSectionOrder"/>)
    /// relative to the sections already present. Existing sections are never
    /// reordered — only the new section's index is chosen — so a loaded file's raw
    /// section order and byte-identity round-trip are preserved. The canonical
    /// order guarantees, in particular, that a freshly created <c>lightmaps</c>
    /// section lands before <c>static_geometry</c>, which RF's two-phase loader
    /// requires (a lightmaps section written after the geometry is skipped and the
    /// level renders black — see <see cref="RflSectionOrder"/>).
    /// </summary>
    public void InsertSection(RflSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        Sections.Insert(RflSectionOrder.InsertionIndex(Sections, section.TypeId), section);
    }

    /// <summary>
    /// Repairs the black-render section-order defect present in early
    /// Glacier-authored files: a <c>lightmaps</c> section (0x1200) written AFTER
    /// <c>static_geometry</c> (0x100). RF's two-phase loader binds every surface to
    /// its lightmap at geometry-load time from a registry that is only populated by
    /// an already-parsed lightmaps section (RF.exe FUN_00460820 / FUN_004ee210); a
    /// lightmaps section that follows the geometry is reached only in the loader's
    /// post-geometry phase, which has no case for it, so it is skipped, the
    /// registry is empty at bind time, and every world face renders black. This
    /// moves the lightmaps section to immediately before static_geometry (RED's
    /// canonical order).
    ///
    /// <para>
    /// Gated on the exact broken pattern — lightmaps present AND positioned after
    /// static_geometry — which NO RED-authored level in the corpus exhibits
    /// (verified across research/example_rfls), so RED files and already-correct
    /// files are left untouched (their byte-identity round-trip is preserved). The
    /// moved section keeps its original bytes; only its position changes. Returns
    /// true when a repair was applied.
    /// </para>
    /// </summary>
    public bool RepairLightmapOrder()
    {
        int geoIndex = Sections.FindIndex(s => s.TypeId == (uint)SectionType.StaticGeometry);
        int lmIndex = Sections.FindIndex(s => s.TypeId == (uint)SectionType.Lightmaps);
        if (geoIndex < 0 || lmIndex < 0 || lmIndex < geoIndex)
        {
            // No geometry, no lightmaps, or lightmaps already precede geometry.
            return false;
        }

        RflSection lightmaps = Sections[lmIndex];
        Sections.RemoveAt(lmIndex);

        // lmIndex > geoIndex, so removing the lightmaps section does not shift
        // geoIndex; inserting there places lightmaps immediately before geometry.
        Sections.Insert(geoIndex, lightmaps);
        return true;
    }

    /// <summary>
    /// User-facing notes about how this file loaded, for every open path (editor, command line, MCP)
    /// to show. Empty for a normal level. Reports a version outside the range Glacier reads, a file
    /// that looks like an interrupted save (header says it holds no sections, or the section table
    /// stops early / has no end marker), and parsed sections stored in a non-standard layout that a
    /// save rewrites. Purely informational: loading and saving behave exactly as without it. Looks
    /// only at sections that are already parsed.
    /// </summary>
    public IReadOnlyList<string> LoadNotes()
    {
        var notes = new List<string>();
        int sectionCount = Sections.Count(s => !s.IsEnd);
        if (IsNewerThanSupported(Header.Version) && sectionCount > 0)
        {
            notes.Add(
                $"This level was saved by a newer version of Alpine Faction (RFL version {Header.Version}; Glacier reads up to " +
                $"{AlpineSaveVersion}). It is open read-only: saving is disabled so its newer content is not lost, and some of " +
                "its content may be missing or read incorrectly.");
        }
        else if (!IsReadableVersion(Header.Version))
        {
            notes.Add(sectionCount == 0
                ? $"This file's version ({Header.Version}) is outside the range Glacier reads (180–200 and 300–306); it opened as an empty level."
                : $"This file's version ({Header.Version}) is outside the range Glacier reads (180–200 and 300–306); some of its content may be missing or read incorrectly.");
        }
        else if (SectionTableIncomplete || (Header.NumSections == 0 && sectionCount > 0))
        {
            notes.Add(sectionCount == 0
                ? "This file looks like an interrupted save (no readable section table); it opened as an empty level."
                : "This file looks like an interrupted save (its section table is incomplete or has no end marker); some content may be missing.");
        }

        foreach (RflSection section in Sections)
        {
            if (section.Content is EventsSection { OmitsAlpineEventOrientation: true } events)
            {
                var classes = new List<string>();
                if (events.OmitsAfTeleportPlayerOrientation)
                {
                    classes.Add("AF_Teleport_Player");
                }

                if (events.OmitsCloneEntityOrientation)
                {
                    classes.Add("Clone_Entity");
                }

                notes.Add(
                    $"This level's {string.Join(" and ", classes)} events were saved by an early Alpine editor " +
                    "without their orientation, a layout Alpine Faction misreads in game. They now use the " +
                    "default orientation; check their facing, then save to store it in the standard layout.");
            }

            switch (section.Content)
            {
                case AlpineLevelPropertiesSection { Version: > AlpineLevelPropertiesSection.CurrentVersion } props:
                    notes.Add(
                        $"This level's Alpine level properties use a pre-release layout (version {props.Version}); saving " +
                        $"rewrites them in the current layout (version {AlpineLevelPropertiesSection.CurrentVersion}).");
                    break;
                case AlpineLightmapsSection { IsUsable: false } lightmaps:
                    notes.Add(lightmaps.SectionVersion != AlpineLightmapsSection.CurrentSectionVersion
                        ? $"This level's Alpine lightmaps are in a pre-release format (section version {lightmaps.SectionVersion}) that " +
                          "Alpine Faction 1.5 ignores: they are kept unchanged, and the level uses its stock lightmaps until it is re-baked."
                        : $"This level's Alpine lightmap chunk could not be read ({lightmaps.StructureProblem}); it is kept unchanged.");
                    break;
                case IRflSectionContent content when AlpineChunkReadOnlyReason(content) is { } reason:
                    notes.Add(
                        $"This level's {SectionIds.Describe(section.TypeId)} chunk is in a layout Glacier does not fully read " +
                        $"({reason}); it is kept unchanged and its objects are read-only.");
                    break;
            }
        }

        return notes;
    }

    /// <summary>Why a typed Alpine record chunk is read-only (see <see cref="AlpineRecordChunkSection{T}"/>), or null.</summary>
    private static string? AlpineChunkReadOnlyReason(IRflSectionContent content) => content switch
    {
        AlpineWeatherRegionsSection s => s.ReadOnlyReason,
        AlpineVehicleFactoriesSection s => s.ReadOnlyReason,
        AlpineProjectionCamerasSection s => s.ReadOnlyReason,
        AlpineRopeEmittersSection s => s.ReadOnlyReason,
        AlpineDirectionalLightsSection s => s.ReadOnlyReason,
        AlpineWaypointHelpersSection s => s.ReadOnlyReason,
        AlpineLiquidEmissionSection s => s.ReadOnlyReason,
        AlpineMoverPathsSection s => s.ReadOnlyReason,
        AlpineTerrainSection s => s.ReadOnlyReason,
        _ => null,
    };
}

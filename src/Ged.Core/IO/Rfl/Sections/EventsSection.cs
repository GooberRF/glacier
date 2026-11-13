using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>events (0x600): generic event records with version-gated rot and color.</summary>
public sealed class EventsSection : IRflSectionContent
{
    /// <summary>The one file version at which the orientation-less Alpine event variants occur.</summary>
    private const int OrientationlessAlpineVariantVersion = 0x12C;

    private const string AfTeleportPlayer = "AF_Teleport_Player";
    private const string CloneEntity = "Clone_Entity";

    public SectionType Type => SectionType.Events;

    public List<RflEvent> Events { get; set; } = new();

    /// <summary>
    /// True when this v300 section stores its <c>AF_Teleport_Player</c> events WITHOUT the
    /// orientation matrix v300 defines for them (see <see cref="OmitsAlpineEventOrientation"/>).
    /// </summary>
    public bool OmitsAfTeleportPlayerOrientation { get; private set; }

    /// <summary>
    /// True when this v300 section stores its <c>Clone_Entity</c> events WITHOUT an orientation
    /// matrix. Alpine started writing (and reading) that matrix at v300 without bumping the level
    /// version (alpinefaction commit 30f6e41c, "fix issues with Clone_Entity"), so v300 levels
    /// saved before that change lack it.
    /// </summary>
    public bool OmitsCloneEntityOrientation { get; private set; }

    /// <summary>
    /// True when this v300 section uses an early-Alpine layout in which <c>AF_Teleport_Player</c>
    /// and/or <c>Clone_Entity</c> events carry no orientation matrix, although current Alpine
    /// Faction reads one for every v300+ level (game_patch/object/event.cpp
    /// level_read_events_patch: <c>read_matrix(…, 300, …)</c>) — so in game such an event, and
    /// every event after it, is misread. Glacier reads the variant as stored (orientation =
    /// identity, the engine's default matrix) and, once the level is saved as v306
    /// (<see cref="RflFile.UpgradeToAlpine"/>), writes the matrix so the game reads it correctly.
    /// </summary>
    public bool OmitsAlpineEventOrientation => OmitsAfTeleportPlayerOrientation || OmitsCloneEntityOrientation;

    /// <summary>
    /// Section parser (registry entry): the strict layout, falling back — for a v300 section
    /// only — to an orientation-less Alpine variant when that consumes the section exactly.
    /// Only levels the strict layout rejects can take the fallback.
    /// </summary>
    public static IRflSectionContent ParseSection(RfReader r, RflContext ctx)
    {
        int start = r.Position;
        Exception? strictError = null;
        EventsSection? strict = null;
        try
        {
            strict = ParseRecords(r, ctx, omitTeleport: false, omitClone: false);
            if (r.Remaining == 0 || ctx.Version != OrientationlessAlpineVariantVersion)
            {
                return strict;
            }
        }
        catch (Exception ex) when (ctx.Version == OrientationlessAlpineVariantVersion)
        {
            strictError = ex;
        }

        int strictEnd = r.Position;

        // Clone_Entity alone first (the documented Alpine change), then AF_Teleport_Player
        // alone, then both.
        foreach ((bool omitTeleport, bool omitClone) in new[] { (false, true), (true, false), (true, true) })
        {
            r.Position = start;
            try
            {
                EventsSection variant = ParseRecords(r, ctx, omitTeleport, omitClone);
                bool usesEveryOmission =
                    (!omitTeleport || variant.Events.Exists(e => e.ClassName == AfTeleportPlayer)) &&
                    (!omitClone || variant.Events.Exists(e => e.ClassName == CloneEntity));
                if (r.Remaining == 0 && usesEveryOmission && variant.Events.TrueForAll(e => IsClassName(e.ClassName)))
                {
                    return variant;
                }
            }
            catch (Exception)
            {
                // Not this variant.
            }
        }

        if (strictError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(strictError).Throw();
        }

        r.Position = strictEnd;
        return strict!;
    }

    /// <summary>Stream parser (also used inside RFG groups): the strict, version-defined layout.</summary>
    public static IRflSectionContent Parse(RfReader r, RflContext ctx) =>
        ParseRecords(r, ctx, omitTeleport: false, omitClone: false);

    /// <summary>A record realigned onto the right boundaries starts with an event class identifier.</summary>
    private static bool IsClassName(string s) =>
        s.Length > 0 && s.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    private bool OmitsRotation(RflEvent e, RflContext ctx) =>
        ctx.Version == OrientationlessAlpineVariantVersion &&
        ((OmitsAfTeleportPlayerOrientation && e.ClassName == AfTeleportPlayer) ||
         (OmitsCloneEntityOrientation && e.ClassName == CloneEntity));

    private static EventsSection ParseRecords(RfReader r, RflContext ctx, bool omitTeleport, bool omitClone)
    {
        var section = new EventsSection
        {
            OmitsAfTeleportPlayerOrientation = omitTeleport,
            OmitsCloneEntityOrientation = omitClone,
        };
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var e = new RflEvent
            {
                Uid = r.ReadI32(),
                ClassName = r.ReadVString(),
                Position = r.ReadVec3(),
                ScriptName = r.ReadVString(),
                HiddenInEditor = r.ReadU8(),
                Delay = r.ReadF32(),
                Bool1 = r.ReadU8(),
                Bool2 = r.ReadU8(),
                Int1 = r.ReadI32(),
                Int2 = r.ReadI32(),
                Float1 = r.ReadF32(),
                Float2 = r.ReadF32(),
                Str1 = r.ReadVString(),
                Str2 = r.ReadVString(),
                Links = r.ReadUidList(),
            };

            if (RflEvent.HasRotation(e.ClassName, ctx.Version) && !section.OmitsRotation(e, ctx))
            {
                e.Rotation = r.ReadMat3();
            }

            if (ctx.EventsHaveColor)
            {
                e.Color = r.ReadColor();
            }

            section.Events.Add(e);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Events.Count);
        foreach (RflEvent e in Events)
        {
            w.WriteI32(e.Uid);
            w.WriteVString(e.ClassName);
            w.WriteVec3(e.Position);
            w.WriteVString(e.ScriptName);
            w.WriteU8(e.HiddenInEditor);
            w.WriteF32(e.Delay);
            w.WriteU8(e.Bool1);
            w.WriteU8(e.Bool2);
            w.WriteI32(e.Int1);
            w.WriteI32(e.Int2);
            w.WriteF32(e.Float1);
            w.WriteF32(e.Float2);
            w.WriteVString(e.Str1);
            w.WriteVString(e.Str2);
            w.WriteUidList(e.Links);

            if (RflEvent.HasRotation(e.ClassName, ctx.Version) && !OmitsRotation(e, ctx))
            {
                w.WriteMat3(e.Rotation ?? Mat3.Identity);
            }

            if (ctx.EventsHaveColor)
            {
                w.WriteColor(e.Color);
            }
        }
    }
}

using Ged.Core.IO;
using Ged.Core.IO.Rfl;

namespace Ged.Core.Model;

/// <summary>
/// A brush (RFL <c>brush</c>): a UID, transform, embedded editable geometry,
/// and flags/life/state. Movers reuse this exact layout.
/// </summary>
public sealed class Brush
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Rotation { get; set; }

    public Geometry Geometry { get; set; } = new();

    /// <summary>Brush flags bitfield (0x01 portal, 0x02 air, 0x04 detail, 0x10 emits_steam).</summary>
    public uint Flags { get; set; }

    public int Life { get; set; }

    /// <summary>0 normal, 2 locked, 3 selected (brush_state enum).</summary>
    public int State { get; set; }

    /// <summary>
    /// RED's per-brush CSG op-type (CDedBrush+0x40). Not part of the RFL brush
    /// record — the constructor default is 1 and the properties dialog never
    /// changes it, so every brush loaded from a file is op-type 1. Preserved on
    /// the model for fidelity; the compiler drives air/solid behaviour from
    /// <see cref="Flags"/> (air ⇒ CSG union, solid ⇒ CSG subtract), which is the
    /// practical realization of RED's mode-3 (op-type 1) path.
    /// </summary>
    public int OpType { get; set; } = 1;

    public static Brush Read(RfReader r, RflContext ctx)
    {
        var b = new Brush
        {
            Uid = r.ReadI32(),
            Position = r.ReadVec3(),
            Rotation = r.ReadMat3(),
            Geometry = Geometry.Parse(r, ctx),
            Flags = r.ReadU32(),
            Life = r.ReadI32(),
            State = r.ReadI32(),
        };
        return b;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Uid);
        w.WriteVec3(Position);
        w.WriteMat3(Rotation);
        Geometry.Write(w, ctx);

        // [ALPINE] The geoable bit (0x20) is an editor-only, in-memory mirror of
        // alpine_level_properties geoable-table membership — RED and Alpine never store
        // geoable in the brush record (editor_patch keeps it only in props.geoable_brush_uids;
        // the game reads the table, not the flag). Mask it out so the brush record round-trips
        // exactly as RED wrote it; the table is the single on-disk source of truth. Every other
        // flag persists verbatim.
        w.WriteU32(Flags & ~(uint)BrushFlags.Geoable);
        w.WriteI32(Life);
        w.WriteI32(State);
    }
}

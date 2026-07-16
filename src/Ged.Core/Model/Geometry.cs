using Ged.Core.IO;
using Ged.Core.IO.Rfl;

namespace Ged.Core.Model;

/// <summary>
/// The RFL <c>geometry</c> structure, shared by compiled static geometry,
/// brushes, and movers. Every documented field — including reserved/unknown
/// blocks and the version-specific face-scroll tables — is preserved so the
/// structure re-serializes byte-for-byte.
/// </summary>
public sealed class Geometry
{
    // Header (version >= 0xC8): two u32s before the name.
    public uint Unknown1 { get; set; }

    public uint Modifiability { get; set; }

    public string Name { get; set; } = string.Empty;

    // Header (version < 0xC8): a single u32 after the name.
    public uint ModifiabilityOld { get; set; }

    public List<string> Textures { get; set; } = new();

    /// <summary>Face-scroll table (version &gt;= 0xB4), keyed by face id.</summary>
    public List<FaceScrollData> FaceScrollData { get; set; } = new();

    // Pre-0xB4 replacement for the face-scroll table: an int count plus
    // count * 0x29 opaque bytes. Preserved verbatim.
    public int PreB4UnknownCount { get; set; }

    public byte[] PreB4UnknownBytes { get; set; } = Array.Empty<byte>();

    public List<Room> Rooms { get; set; } = new();

    public List<SubroomList> SubroomLists { get; set; } = new();

    public List<Portal> Portals { get; set; } = new();

    public List<Vec3> Vertices { get; set; } = new();

    public List<Face> Faces { get; set; } = new();

    public List<Surface> Surfaces { get; set; } = new();

    /// <summary>Legacy face-scroll table written after surfaces (version &lt;= 0xB4).</summary>
    public List<FaceScrollData> LegacyFaceScrollData { get; set; } = new();

    public static Geometry Parse(RfReader r, RflContext ctx)
    {
        var g = new Geometry();

        if (ctx.GeometryHasNewModifiability)
        {
            g.Unknown1 = r.ReadU32();
            g.Modifiability = r.ReadU32();
        }

        g.Name = r.ReadVString();

        if (!ctx.GeometryHasNewModifiability)
        {
            g.ModifiabilityOld = r.ReadU32();
        }

        int numTextures = r.ReadI32();
        for (int i = 0; i < numTextures; i++)
        {
            g.Textures.Add(r.ReadVString());
        }

        if (ctx.HasFaceScrollData)
        {
            int n = r.ReadI32();
            for (int i = 0; i < n; i++)
            {
                g.FaceScrollData.Add(ReadScroll(r));
            }
        }
        else
        {
            g.PreB4UnknownCount = r.ReadI32();
            g.PreB4UnknownBytes = r.ReadBytes(g.PreB4UnknownCount * 0x29);
        }

        int numRooms = r.ReadI32();
        for (int i = 0; i < numRooms; i++)
        {
            g.Rooms.Add(ReadRoom(r, ctx));
        }

        int numSubroomLists = r.ReadI32();
        for (int i = 0; i < numSubroomLists; i++)
        {
            var sl = new SubroomList { RoomIndex = r.ReadI32() };
            int numSub = r.ReadI32();
            for (int j = 0; j < numSub; j++)
            {
                sl.SubroomIndices.Add(r.ReadI32());
            }

            g.SubroomLists.Add(sl);
        }

        int numPortals = r.ReadI32();
        for (int i = 0; i < numPortals; i++)
        {
            g.Portals.Add(new Portal
            {
                RoomIndex1 = r.ReadI32(),
                RoomIndex2 = r.ReadI32(),
                Point1 = r.ReadVec3(),
                Point2 = r.ReadVec3(),
            });
        }

        int numVertices = r.ReadI32();
        for (int i = 0; i < numVertices; i++)
        {
            g.Vertices.Add(r.ReadVec3());
        }

        int numFaces = r.ReadI32();
        for (int i = 0; i < numFaces; i++)
        {
            g.Faces.Add(ReadFace(r));
        }

        int numSurfaces = r.ReadI32();
        for (int i = 0; i < numSurfaces; i++)
        {
            g.Surfaces.Add(ReadSurface(r));
        }

        if (ctx.HasLegacyFaceScrollData)
        {
            int n = r.ReadI32();
            for (int i = 0; i < n; i++)
            {
                g.LegacyFaceScrollData.Add(ReadScroll(r));
            }
        }

        return g;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        if (ctx.GeometryHasNewModifiability)
        {
            w.WriteU32(Unknown1);
            w.WriteU32(Modifiability);
        }

        w.WriteVString(Name);

        if (!ctx.GeometryHasNewModifiability)
        {
            w.WriteU32(ModifiabilityOld);
        }

        w.WriteI32(Textures.Count);
        foreach (string tex in Textures)
        {
            w.WriteVString(tex);
        }

        if (ctx.HasFaceScrollData)
        {
            w.WriteI32(FaceScrollData.Count);
            foreach (FaceScrollData s in FaceScrollData)
            {
                WriteScroll(w, s);
            }
        }
        else
        {
            w.WriteI32(PreB4UnknownCount);
            w.WriteBytes(PreB4UnknownBytes);
        }

        w.WriteI32(Rooms.Count);
        foreach (Room room in Rooms)
        {
            WriteRoom(w, ctx, room);
        }

        w.WriteI32(SubroomLists.Count);
        foreach (SubroomList sl in SubroomLists)
        {
            w.WriteI32(sl.RoomIndex);
            w.WriteI32(sl.SubroomIndices.Count);
            foreach (int idx in sl.SubroomIndices)
            {
                w.WriteI32(idx);
            }
        }

        w.WriteI32(Portals.Count);
        foreach (Portal p in Portals)
        {
            w.WriteI32(p.RoomIndex1);
            w.WriteI32(p.RoomIndex2);
            w.WriteVec3(p.Point1);
            w.WriteVec3(p.Point2);
        }

        w.WriteI32(Vertices.Count);
        foreach (Vec3 v in Vertices)
        {
            w.WriteVec3(v);
        }

        w.WriteI32(Faces.Count);
        foreach (Face f in Faces)
        {
            WriteFace(w, f);
        }

        w.WriteI32(Surfaces.Count);
        foreach (Surface s in Surfaces)
        {
            WriteSurface(w, s);
        }

        if (ctx.HasLegacyFaceScrollData)
        {
            w.WriteI32(LegacyFaceScrollData.Count);
            foreach (FaceScrollData s in LegacyFaceScrollData)
            {
                WriteScroll(w, s);
            }
        }
    }

    private static FaceScrollData ReadScroll(RfReader r) => new()
    {
        FaceId = r.ReadI32(),
        UVelocity = r.ReadF32(),
        VVelocity = r.ReadF32(),
    };

    private static void WriteScroll(RfWriter w, FaceScrollData s)
    {
        w.WriteI32(s.FaceId);
        w.WriteF32(s.UVelocity);
        w.WriteF32(s.VVelocity);
    }

    private static Room ReadRoom(RfReader r, RflContext ctx)
    {
        var room = new Room
        {
            Id = r.ReadI32(),
            Aabb = r.ReadAabb(),
            IsSkyroom = r.ReadU8(),
            IsCold = r.ReadU8(),
            IsOutside = r.ReadU8(),
            IsAirlock = r.ReadU8(),
            IsLiquidRoom = r.ReadU8(),
            HasAmbientLight = r.ReadU8(),
            IsSubroom = r.ReadU8(),
            HasAlpha = r.ReadU8(),
            Life = r.ReadF32(),
        };

        if (ctx.RoomsHaveEax)
        {
            room.EaxEffect = r.ReadVString();
        }

        if (room.IsLiquidRoom != 0)
        {
            room.LiquidProperties = new RoomLiquidProperties
            {
                Depth = r.ReadF32(),
                Color = r.ReadColor(),
                SurfaceTexture = r.ReadVString(),
                Visibility = r.ReadF32(),
                LiquidType = r.ReadI32(),
                LiquidAlpha = r.ReadI32(),
                ContainsPlankton = r.ReadU8(),
                TexturePixelsPerMeterU = r.ReadI32(),
                TexturePixelsPerMeterV = r.ReadI32(),
                TextureAngleRadians = r.ReadF32(),
                Waveform = r.ReadI32(),
                TextureScrollRate = r.ReadUv(),
            };
        }

        if (room.HasAmbientLight != 0)
        {
            room.AmbientColor = r.ReadColor();
        }

        return room;
    }

    private static void WriteRoom(RfWriter w, RflContext ctx, Room room)
    {
        w.WriteI32(room.Id);
        w.WriteAabb(room.Aabb);
        w.WriteU8(room.IsSkyroom);
        w.WriteU8(room.IsCold);
        w.WriteU8(room.IsOutside);
        w.WriteU8(room.IsAirlock);
        w.WriteU8(room.IsLiquidRoom);
        w.WriteU8(room.HasAmbientLight);
        w.WriteU8(room.IsSubroom);
        w.WriteU8(room.HasAlpha);
        w.WriteF32(room.Life);

        if (ctx.RoomsHaveEax)
        {
            w.WriteVString(room.EaxEffect ?? string.Empty);
        }

        if (room.IsLiquidRoom != 0)
        {
            RoomLiquidProperties lp = room.LiquidProperties!;
            w.WriteF32(lp.Depth);
            w.WriteColor(lp.Color);
            w.WriteVString(lp.SurfaceTexture);
            w.WriteF32(lp.Visibility);
            w.WriteI32(lp.LiquidType);
            w.WriteI32(lp.LiquidAlpha);
            w.WriteU8(lp.ContainsPlankton);
            w.WriteI32(lp.TexturePixelsPerMeterU);
            w.WriteI32(lp.TexturePixelsPerMeterV);
            w.WriteF32(lp.TextureAngleRadians);
            w.WriteI32(lp.Waveform);
            w.WriteUv(lp.TextureScrollRate);
        }

        if (room.HasAmbientLight != 0)
        {
            w.WriteColor(room.AmbientColor!.Value);
        }
    }

    /// <summary>
    /// Per-vertex lightmap UVs are present only for faces that bind a real
    /// lightmap surface. The "no surface" sentinel appears as both -1
    /// (0xFFFFFFFF) and 0xFFFF in the corpus, so the low 16 bits are tested:
    /// a surface index whose low word is 0xFFFF carries no lightmap UVs.
    /// </summary>
    private static bool FaceHasLightmapUvs(Face f) => (f.SurfaceIndex & 0xFFFF) != 0xFFFF;

    private static Face ReadFace(RfReader r)
    {
        var f = new Face
        {
            Plane = r.ReadPlane(),
            Texture = r.ReadI32(),
            SurfaceIndex = r.ReadI32(),
            FaceId = r.ReadI32(),
            Reserved1A = r.ReadI32(),
            Reserved1B = r.ReadI32(),
            PortalIndexPlus2 = r.ReadI32(),
            Flags = r.ReadU16(),
            Reserved2 = r.ReadU16(),
            SmoothingGroups = r.ReadU32(),
            RoomIndex = r.ReadI32(),
        };

        int numVertices = r.ReadI32();
        bool hasLightmap = FaceHasLightmapUvs(f);
        for (int i = 0; i < numVertices; i++)
        {
            var fv = new FaceVertex
            {
                Index = r.ReadI32(),
                TextureCoords = r.ReadUv(),
            };
            if (hasLightmap)
            {
                fv.LightmapCoords = r.ReadUv();
            }

            f.Vertices.Add(fv);
        }

        return f;
    }

    private static void WriteFace(RfWriter w, Face f)
    {
        w.WritePlane(f.Plane);
        w.WriteI32(f.Texture);
        w.WriteI32(f.SurfaceIndex);
        w.WriteI32(f.FaceId);
        w.WriteI32(f.Reserved1A);
        w.WriteI32(f.Reserved1B);
        w.WriteI32(f.PortalIndexPlus2);
        w.WriteU16(f.Flags);
        w.WriteU16(f.Reserved2);
        w.WriteU32(f.SmoothingGroups);
        w.WriteI32(f.RoomIndex);

        w.WriteI32(f.Vertices.Count);
        bool hasLightmap = FaceHasLightmapUvs(f);
        foreach (FaceVertex fv in f.Vertices)
        {
            w.WriteI32(fv.Index);
            w.WriteUv(fv.TextureCoords);
            if (hasLightmap)
            {
                w.WriteUv(fv.LightmapCoords ?? default);
            }
        }
    }

    private static Surface ReadSurface(RfReader r) => new()
    {
        LightmapIndex = r.ReadI32(),
        X = r.ReadU8(),
        Y = r.ReadU8(),
        W = r.ReadU8(),
        H = r.ReadU8(),
        XPixelsPerMeter = r.ReadF32(),
        YPixelsPerMeter = r.ReadF32(),
        BoundingBox = r.ReadAabb(),
        Plane = r.ReadPlane(),
        ShouldSmooth = r.ReadI32(),
        UnknownZero = r.ReadI32(),
        DroppedCoefficient = r.ReadI32(),
        UCoefficient = r.ReadI32(),
        VCoefficient = r.ReadI32(),
        UvAdd = r.ReadUv(),
        UvScale = r.ReadUv(),
        RoomIndex = r.ReadI32(),
    };

    private static void WriteSurface(RfWriter w, Surface s)
    {
        w.WriteI32(s.LightmapIndex);
        w.WriteU8(s.X);
        w.WriteU8(s.Y);
        w.WriteU8(s.W);
        w.WriteU8(s.H);
        w.WriteF32(s.XPixelsPerMeter);
        w.WriteF32(s.YPixelsPerMeter);
        w.WriteAabb(s.BoundingBox);
        w.WritePlane(s.Plane);
        w.WriteI32(s.ShouldSmooth);
        w.WriteI32(s.UnknownZero);
        w.WriteI32(s.DroppedCoefficient);
        w.WriteI32(s.UCoefficient);
        w.WriteI32(s.VCoefficient);
        w.WriteUv(s.UvAdd);
        w.WriteUv(s.UvScale);
        w.WriteI32(s.RoomIndex);
    }
}

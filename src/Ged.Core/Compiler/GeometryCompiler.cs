using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// The geometry build pipeline: brush booleans (time-ordered) → portal chopping
/// → room building → detail subrooms → room effects → t-joints → lightmap
/// surfaces, producing a compiled <see cref="Geometry"/> plus a build report.
/// Functional parity with RED.exe (the output loads and plays correctly in
/// RF.exe), not byte-identity. Pure and deterministic; no GPU/VFS dependency.
/// </summary>
public sealed class GeometryCompiler
{
    /// <summary>Synthetic room ids for rooms without a room-effect count down from here.</summary>
    private const uint SyntheticRoomIdBase = 0x7FFFFFFEu;

    private readonly IReadOnlyList<Brush> _brushes;
    private readonly IReadOnlyList<RoomEffect> _effects;
    private readonly CompileOptions _options;
    private readonly BuildReport _report = new();

    private GeometryCompiler(IReadOnlyList<Brush> brushes, IReadOnlyList<RoomEffect> effects, CompileOptions options)
    {
        _brushes = brushes;
        _effects = effects;
        _options = options;
    }

    /// <summary>Compiles a level's brushes (in document order) and room effects into static geometry.</summary>
    public static CompiledLevel Compile(
        IReadOnlyList<Brush> brushes,
        IReadOnlyList<RoomEffect>? effects = null,
        CompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        return new GeometryCompiler(brushes, effects ?? Array.Empty<RoomEffect>(), options ?? new CompileOptions()).Run();
    }

    private CompiledLevel Run()
    {
        var sw = Stopwatch.StartNew();
        var result = new CompiledLevel { Report = _report };

        // Pre-build validation (red-status style warnings; never blocks the build).
        BrushValidator.Validate(_brushes, _report);

        // Phase 1: classify brushes, assign stable sequential face ids, convert to world space.
        var detailFaces = new List<CsgFace>();
        var portalBrushes = new List<(Brush Brush, List<CsgFace> Faces)>();
        var solidBrushes = new List<(Brush Brush, bool IsAir, List<CsgFace> Faces)>();
        var detailBrushes = new List<(Brush Brush, List<CsgFace> Faces)>();
        var csgFaceStart = new Dictionary<int, int>(); // brush uid → first FaceId (CSG participants)
        int faceIdCursor = 0;
        int uidHigh = 0;

        var traitCache = new Dictionary<string, TextureTraits>();

        foreach (Brush b in _brushes)
        {
            _options.Cancellation.ThrowIfCancellationRequested();
            uidHigh = Math.Max(uidHigh, b.Uid);
            int faceIdStart = faceIdCursor;
            List<CsgFace> faces = BrushWorld.ToWorldFaces(b, faceIdCursor, out faceIdCursor);
            var flags = (BrushFlags)b.Flags;

            // RED honors texture alpha (0x40) / holes (0x80) ONLY on detail faces; on a
            // structural (non-detail) brush the alpha channel is ignored and the face draws
            // opaque. A detail brush is a non-portal brush carrying Detail or Geoable.
            bool detailBrush = (flags & BrushFlags.Portal) == 0
                && (flags & (BrushFlags.Detail | BrushFlags.Geoable)) != 0;
            ApplyTextureTraits(faces, traitCache, detailBrush);

            if ((flags & BrushFlags.Portal) != 0)
            {
                portalBrushes.Add((b, faces));

                // RED's boolean loop runs every op-1 brush BEFORE portal chopping, so
                // an air portal box carves its own doorway volume (dm06's corridors
                // connect through 1 m gaps bridged only by the portal brush). Flat
                // solid portal quads have no volume and are boolean no-ops.
                if ((flags & BrushFlags.Air) != 0)
                {
                    var carve = new List<CsgFace>(faces.Count);
                    foreach (CsgFace f in faces)
                    {
                        CsgFace clone = f.With(new List<CsgVertex>(f.Vertices));
                        clone.IsPortal = false; // participates as a normal air operand
                        carve.Add(clone);
                    }

                    solidBrushes.Add((b, true, carve));
                }
            }
            else if ((flags & (BrushFlags.Detail | BrushFlags.Geoable)) != 0)
            {
                foreach (CsgFace f in faces)
                {
                    f.Flags |= (ushort)FaceFlags.IsDetail;
                }

                detailFaces.AddRange(faces);
                detailBrushes.Add((b, faces));
            }
            else
            {
                solidBrushes.Add((b, (flags & BrushFlags.Air) != 0, faces));

                // Item 7: only these plain air/solid brushes can lose faces to the
                // boolean solve — record where their FaceId range starts so surviving
                // fragments map back to local face indices.
                csgFaceStart[b.Uid] = faceIdStart;
                result.BrushFaceIdStart[b.Uid] = faceIdStart; // item 5: fragment→brush-face mapping
                result.SurvivingBrushFaces[b.Uid] = new bool[b.Geometry.Faces.Count];
            }
        }

        // Phase 2: CSG. Open space = the time-ordered fold of air (union) and solid
        // (subtract) brushes; the survival solver returns the open/solid boundary faces,
        // already oriented so normals point into open space (RF's convention).
        int step = 0;
        int total = solidBrushes.Count + portalBrushes.Count;
        var solver = new CsgSolver
        {
            UseWorldBsp = _options.UseWorldBsp,
            UseLeafExtraction = _options.UseLeafExtraction,
            SourceFaceEmission = _options.SourceFaceEmission,
            IncrementalAccumulator = _options.IncrementalAccumulator,
            BRepBoundary = _options.BRepBoundary,
            PartitionClip = _options.PartitionClip,
            GlobalPartition = _options.GlobalPartition,
            SharedBsp = _options.SharedBsp,
            BoundedVolumeClip = _options.BoundedVolumeClip,
            FusedPartition = _options.FusedPartition,
            EdgeLerpSplit = _options.EdgeLerpSplit,
            EdgeMergeTolerance = _options.EdgeMergeTolerance,
            RegionWiseCoincidence = _options.RegionWiseCoincidence,
        };
        foreach ((Brush _, bool isAir, List<CsgFace> faces) in solidBrushes)
        {
            _options.Cancellation.ThrowIfCancellationRequested();
            _options.Progress?.Invoke(new CompileProgress("Adding brush", ++step, total));
            solver.AddBrush(isAir, faces);
        }

        // Memory/perf instrumentation for the CSG solve (world-BSP budget discipline — the 11.6 GB lesson).
        long allocBefore = GC.GetTotalAllocatedBytes();
        var solveSw = Stopwatch.StartNew();
        List<CsgFace> open = solver.Solve();
        solveSw.Stop();
        _report.SolveMs = solveSw.Elapsed.TotalMilliseconds;
        _report.SolveAllocBytes = GC.GetTotalAllocatedBytes() - allocBefore;
        _report.SolvePeakHeapBytes = GC.GetTotalMemory(false);
        _report.WorldBspUsed = solver.WorldBspActive;
        _report.WorldBspBudgetExceeded = solver.WorldBspBudgetExceeded;
        _report.WorldBspNodes = solver.WorldBspNodes;
        _report.WorldBspLeaves = solver.WorldBspLeaves;
        _report.WorldBspFragments = solver.WorldBspFragments;
        _report.LeafExtractionUsed = solver.LeafExtractionActive;
        _report.FusedPartitionUsed = solver.FusedPartitionActive;
        _report.IncrementalUsed = solver.IncrementalActive;
        _report.SharedBspUsed = solver.SharedBspActive;
        _report.EdgeLerpSplitUsed = solver.EdgeLerpSplitActive;
        _report.EdgeSharedVertices = solver.EdgeSharedVertices;
        _report.EdgeCornerMerges = solver.EdgeCornerMerges;
        _report.IncWorldFaces = solver.IncWorldFaces;
        _report.IncDissolved = solver.IncDissolved;
        _report.BRepCapCuts = solver.BRepCapCuts;
        _report.ExtractedPortals = solver.ExtractedPortals;
        _report.SfVerbatim = solver.SfVerbatim;
        _report.SfCrossed = solver.SfCrossed;
        _report.SfDropped = solver.SfDropped;
        _report.AttributedByContainment = solver.AttributedByContainment;
        _report.AttributedByNearest = solver.AttributedByNearest;
        _report.Unattributed = solver.Unattributed;
        _report.LeafDegenerate = solver.LeafDegenerate;
        _report.LeafMaxCons = solver.LeafMaxCons;
        _report.LeafOverCap = solver.LeafOverCap;
        _report.DecomposedBrushes = solver.DecomposedBrushes;
        _report.DecompFallbackBrushes = solver.DecompFallbackBrushes;
        _report.DecompTotalPieces = solver.DecompTotalPieces;
        _report.DecompMaxPieces = solver.DecompMaxPieces;
        DropDegenerate(open);
        DropDegenerate(detailFaces);

        // Item 7: mark each source brush face that kept at least one fragment in the
        // open set (fragments inherit SourceBrushUid + FaceId through every split).
        // Faces with no surviving fragment were clipped away — outside the level or
        // consumed by CSG — and the brush overlays can hide them.
        foreach (CsgFace f in open)
        {
            if (result.SurvivingBrushFaces.TryGetValue(f.SourceBrushUid, out bool[]? bits)
                && csgFaceStart.TryGetValue(f.SourceBrushUid, out int start))
            {
                int local = f.FaceId - start;
                if (local >= 0 && local < bits.Length)
                {
                    bits[local] = true;
                }
            }
        }

        // Phase 3: portals — chop each portal brush's opening to the open
        // cross-section, insert the membrane pairs, then chop the WORLD faces that
        // cross a membrane (mode-4 semantics: no face spans a doorway sheet, so the
        // room flood can separate the two sides exactly at the portal).
        var portalPass = new PortalBuilder(_report);
        portalPass.InsertPortalFaces(open, portalBrushes, solver, ref step, total, _options);
        portalPass.ChopWorldFaces(open);

        // Combine world + detail faces for output. Detail faces already face into open space.
        var allFaces = new List<CsgFace>(open.Count + detailFaces.Count);
        allFaces.AddRange(open);
        allFaces.AddRange(detailFaces);

        // Weld a shared vertex pool. CSG splitting leaves T-junctions (a split edge
        // on one face has no matching vertex on the un-split neighbour); the t-joint
        // pass inserts those vertices so the mesh is edge-manifold — required for both
        // seam-free rendering and reliable room flood fill.
        var welder = new VertexWelder();
        foreach (CsgFace f in allFaces)
        {
            foreach (CsgVertex v in f.Vertices)
            {
                welder.Add(v.Position);
            }
        }

        if (_options.FixTJoints)
        {
            _options.Progress?.Invoke(new CompileProgress("Fixing any t-joints", 0, 1));
            TJointFixer.Fix(allFaces, welder.Vertices);

            // Seal residual open seams: GED's independent per-face exact splitter leaves the
            // shared boundary of two faces welded a fraction of a millimetre apart (a leak RED's
            // shared BSP never produces). SeamSealer welds/stitches those open edges to a fixed
            // point — RED's t-joint fixer generalised to the near-coincident / partial-overlap
            // cases. Runs on the merged wall set (world + detail already combined above).
            // The leaf-extraction path re-tessellates every face through the global partition, which
            // leaves over-determined-corner near-pairs up to a few mm apart (after the plane-aware
            // StationWeld); it seals at a wider tolerance to close them. The per-brush default keeps
            // RED's tight 1 mm — its geometry is source-face-preserving and needs no wider bridge.
            // The incremental accumulator keeps RED's tight 1 mm like the per-brush default: its geometry is
            // source-face-preserving with shared registry cuts, so only the sub-mm divergent-triple station
            // cohort needs sealing. Measured NOT removable on it either (dm04 30→117, ctf01 31→269,
            // dmabrupt 10→88 with the sealer off) — the sealer is essential on every CSG path.
            float sealTol = _options.SealTolerance
                ?? (_options.UseLeafExtraction || _options.FusedPartition || _options.IncrementalAccumulator ? 3e-3f : 1e-3f);
            if (sealTol > 0f)
            {
                SeamSealer.Seal(allFaces, sealTol);
            }
        }

        // Pool indices per face (t-joint inserts reuse existing pool positions).
        var facePoolIndices = new List<int[]>(allFaces.Count);
        foreach (CsgFace f in allFaces)
        {
            var idx = new int[f.Vertices.Count];
            for (int i = 0; i < f.Vertices.Count; i++)
            {
                idx[i] = welder.Add(f.Vertices[i].Position);
            }

            facePoolIndices.Add(idx);
        }

        // Phase 4: rooms — edge-adjacency flood fill (connected open cells), split
        // at portal membranes by the plane-side vote.
        _options.Progress?.Invoke(new CompileProgress("Building rooms", 0, 1));
        var roomBuilder = new RoomBuilder(_report);
        RoomBuildResult rooms = roomBuilder.Build(
            allFaces, facePoolIndices, welder.Vertices, open.Count, solidBrushes, detailBrushes,
            portalPass.Membranes, _effects, _options.Alpine, _options.PortalFaceVote);

        // Phase 5: portal records between rooms; membranes that divided nothing
        // stay untagged and are dropped so no stray portal faces reach the file.
        portalPass.BuildRecords(rooms);
        CompactDroppedPortalFaces(allFaces, facePoolIndices, rooms);

        // Phase 6: liquid surfaces — a clipped water surface per liquid room (mode-6 equivalent).
        LiquidSurfaceBuilder.Insert(allFaces, facePoolIndices, welder, rooms, solver);

        // Phase 6b: output-stage coplanar merge (flagship 22) — re-merge each source face's coplanar kin
        // (the fold's in-place split slivers) back into maximal convex faces, keyed on the shared vertex-pool
        // indices so it is watertight by construction (removes only edges used by exactly two kin). Cuts the
        // per-room face count toward RED's without touching holes, portals, or the water surface.
        if (_options.MergeCoplanarOutput)
        {
            _report.CoplanarMerged = OutputFaceMerger.Merge(allFaces, facePoolIndices, welder.Vertices);
        }

        // Phase 6c: RED-faithful per-output-face vertex cleanup (BuildFinalRenderSolid FUN_00496150) —
        // drop repeated + redundant-collinear vertices from detail (geoable/breakable) faces so the
        // in-game geomod cap triangulator (Alpine ear_clip_triangulate) does not stall on them. Keeps
        // load-bearing T-junction corners, so it is watertight by construction.
        if (_options.CleanOutputFaces)
        {
            _report.OutputFacesCleaned = OutputFaceCleanup.Clean(allFaces, facePoolIndices, welder.Vertices);
        }

        // Phase 7: surfaces + lightmap atlas.
        var surfaces = new List<Surface>();
        SurfaceBuildResult? surfaceResult = null;
        if (_options.BuildSurfaces)
        {
            _options.Progress?.Invoke(new CompileProgress("Calculating lightmap UVs", 0, 1));
            surfaceResult = new SurfaceBuilder(_options.HighResLightmaps).Build(allFaces, rooms, result, _options.GroupSurfaces);
            surfaces = surfaceResult.Surfaces;
        }

        // Phase 7b: bake real lighting into the atlas (opt-in).
        if (_options.BuildSurfaces && _options.BakeLighting && surfaceResult is not null)
        {
            _options.Progress?.Invoke(new CompileProgress("Calculating lighting", 0, 1));
            result.BakeStats = LightingBaker.Bake(allFaces, surfaceResult, rooms, result, _options, _report);
        }

        // Face-scroll table: authored scroll velocities keyed by compiled face id.
        List<FaceScrollData> scroll = FaceScrollTable.Build(allFaces, _brushes);

        // Phase 8: assemble the output geometry.
        var assembler = new GeometryAssembler();
        result.Geometry = assembler.Assemble(
            allFaces, facePoolIndices, welder.Vertices, rooms, portalPass.Portals, surfaces, scroll);

        // Alpine geoable/breakable brush → compiled-room-uid map (recomputed every build so
        // the alpine_level_properties tables the game reads track the rebuilt room UIDs).
        if (_options.Alpine)
        {
            result.AlpineBuild = true;
            if (_options.IsolatedBrushUids.Count > 0)
            {
                AlpineIsolation.RecordLinks(_options.IsolatedBrushUids, rooms, result);
            }
        }

        FillReport(result, uidHigh);
        sw.Stop();
        _report.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>
    /// Sliver rejection width (item 5): a fragment whose mean width (2·area/perimeter) is under
    /// half a millimetre is CSG split noise, not authored geometry. RED frees zero-area fragments
    /// at split time (FUN_0048a8b0) and its 1e-4 epsilons collapse such slivers; GED's exact
    /// splitter keeps them alive as near-zero-area ribbons. Left in, each sliver becomes its own
    /// portal-less junk room (dmabruptdecay: 34 singleton main rooms) — and when RF's smallest-
    /// volume point-in-room lookup lands the camera in one, the portal flood renders nothing:
    /// the reported in-game "missing brushwork".
    /// </summary>
    private const float MinFragmentWidth = 5e-4f;

    private static void DropDegenerate(List<CsgFace> faces)
    {
        faces.RemoveAll(static f =>
        {
            if (f.Vertices.Count < 3)
            {
                return true;
            }

            float area = f.Area();
            if (area < 1e-6f)
            {
                return true;
            }

            float perimeter = 0f;
            for (int i = 0; i < f.Vertices.Count; i++)
            {
                Vec3 a = f.Vertices[i].Position;
                Vec3 b = f.Vertices[(i + 1) % f.Vertices.Count].Position;
                perimeter += b.Sub(a).Length();
            }

            return perimeter > 1e-6f && (2f * area) / perimeter < MinFragmentWidth;
        });
    }

    /// <summary>
    /// Removes portal membrane faces that produced no portal record (they divide
    /// nothing) — RF must never see a portal face without a record. Compacts the
    /// face list, pool-index list, and room map in lockstep.
    /// </summary>
    private static void CompactDroppedPortalFaces(
        List<CsgFace> faces, List<int[]> facePoolIndices, RoomBuildResult rooms)
    {
        int write = 0;
        var faceRoom = rooms.FaceRoom;
        for (int read = 0; read < faces.Count; read++)
        {
            CsgFace f = faces[read];
            if (f.IsPortal && (f.PortalIndexPlus2 < 2 || f.RoomIndex < 0))
            {
                continue;
            }

            faces[write] = f;
            facePoolIndices[write] = facePoolIndices[read];
            faceRoom[write] = faceRoom[read];
            write++;
        }

        faces.RemoveRange(write, faces.Count - write);
        facePoolIndices.RemoveRange(write, facePoolIndices.Count - write);
        Array.Resize(ref faceRoom, write);
        rooms.FaceRoom = faceRoom;
    }

    /// <summary>
    /// ORs texture-derived flag bits (invisible / alpha / holes) into each face,
    /// matching RED's compile-time texture inspection (RED.exe FlagFaceTextureTraits
    /// FUN_0041d3c0). Uses the options provider when available, else the *_invisible*
    /// name fallback. Applied before the CSG solve so split fragments inherit the bits.
    /// <para>
    /// RED gates the alpha (0x40) and holes (0x80) bits behind the detail bit (0x08):
    /// the setters at FUN_0041e470 / FUN_0041e490 run only inside <c>if ((flags &gt;&gt; 3) &amp; 1)</c>.
    /// So on a structural (non-detail) brush the texture's alpha channel is ignored and the
    /// face draws opaque — matching RED-built levels (a sign on a normal solid wall is opaque;
    /// the same texture on a detail brush alpha-blends). The invisible bit is name-driven and
    /// ungated, exactly as RED does it.
    /// </para>
    /// </summary>
    private void ApplyTextureTraits(List<CsgFace> faces, Dictionary<string, TextureTraits> cache, bool detailBrush)
    {
        foreach (CsgFace f in faces)
        {
            if (string.IsNullOrEmpty(f.Texture))
            {
                continue;
            }

            if (!cache.TryGetValue(f.Texture, out TextureTraits traits))
            {
                traits = _options.TextureTraits?.Invoke(f.Texture) ?? TextureTraits.None;

                // RF's *_invisible* wall/collision textures are often plain opaque
                // TGAs — invisible by naming convention, not by alpha content — so
                // the name rule applies on top of whatever the pixels say.
                TextureTraits byName = TextureTraits.FromName(f.Texture);
                traits = new TextureTraits(
                    traits.IsInvisible || byName.IsInvisible,
                    traits.HasAlpha || byName.HasAlpha,
                    traits.HasHoles || byName.HasHoles);
                cache[f.Texture] = traits;
            }

            if (traits.IsInvisible)
            {
                f.Flags |= (ushort)FaceFlags.IsInvisible;
            }

            // Alpha / holes are honored ONLY on detail faces (RED's detail-bit gate). RED's
            // FlagFaceTextureTraits (FUN_0041d3c0) CLEARS 0x40/0x80 up front and only re-sets
            // them inside the detail gate (FUN_0041e4b0 == flags>>3 & 1), so a structural face
            // is always opaque no matter the texture — and no matter what a stale authored bit
            // says. Reproduce both halves: derive on detail faces, force-clear on structural
            // faces so an imported/legacy-authored 0x40/0x80 can never survive onto a wall.
            if (detailBrush)
            {
                if (traits.HasAlpha)
                {
                    f.Flags |= (ushort)FaceFlags.HasAlpha;
                }

                if (traits.HasHoles)
                {
                    f.Flags |= (ushort)FaceFlags.HasHoles;
                }
            }
            else
            {
                f.Flags = (ushort)(f.Flags & ~(ushort)(FaceFlags.HasAlpha | FaceFlags.HasHoles));
            }
        }
    }

    private void FillReport(CompiledLevel result, int uidHigh)
    {
        Geometry g = result.Geometry;
        _report.Brushes = _brushes.Count;
        _report.Rooms = g.Rooms.Count;
        int sub = 0;
        foreach (Room r in g.Rooms)
        {
            if (r.IsSubroom != 0)
            {
                sub++;
            }
        }

        _report.Subrooms = sub;
        _report.Portals = g.Portals.Count;
        _report.Faces = g.Faces.Count;
        int fv = 0;
        foreach (Face f in g.Faces)
        {
            fv += f.Vertices.Count;
        }

        _report.FaceVertices = fv;
        _report.Vertices = g.Vertices.Count;
        _report.Surfaces = g.Surfaces.Count;
        _report.LightmapPages = result.Lightmaps.Count;
        _report.Uids = uidHigh;
    }

    /// <summary>Synthetic id for room slot <paramref name="index"/> when no effect claims it.</summary>
    internal static int SyntheticRoomId(int index) => unchecked((int)(SyntheticRoomIdBase - (uint)index));
}

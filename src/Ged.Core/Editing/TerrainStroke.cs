using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The Terrain Tools settings Alpine RED keeps for its panel (editor_patch/terrain_paint.cpp:35-46): the tool, the
/// selected entry of the Layer list (the base layers, then the overlays, then the decorations), radius in metres,
/// strength 0..1, falloff, dab spacing as a share of the radius, the Set Height target (world Y), the Ramp angle and the
/// mirror lines.
/// </summary>
public sealed class TerrainBrushSettings
{
    public TerrainTool Tool { get; set; } = TerrainTool.PaintLayer;

    /// <summary>Index into the Layer list: base layers first, then overlays, then decorations.</summary>
    public int Layer { get; set; }

    public float Radius { get; set; } = 8.0f;

    public float Strength { get; set; } = 0.5f;

    public TerrainFalloff Falloff { get; set; } = TerrainFalloff.Smooth;

    /// <summary>Dab spacing as a share of the radius (0.05 .. 2; terrain_paint.cpp:1756-1758).</summary>
    public float Spacing { get; set; } = 0.25f;

    /// <summary>Set Height's target, world Y.</summary>
    public float SetHeight { get; set; }

    /// <summary>Ramp slope in degrees (negative goes down), rising along the drag.</summary>
    public float RampAngle { get; set; } = 15.0f;

    public bool MirrorX { get; set; }

    public bool MirrorZ { get; set; }

    /// <summary>Mirror line offsets from the terrain's centre, in half cells.</summary>
    public int MirrorXOffset2 { get; set; }

    public int MirrorZOffset2 { get; set; }

    public TerrainBrushSettings Clone() => (TerrainBrushSettings)MemberwiseClone();

    /// <summary>Clamps every field into the panel's ranges (terrain_paint.cpp:68-70, 1745-1770).</summary>
    public void Clamp()
    {
        Radius = AlpineTerrainRules.ClampFinite(Radius, TerrainBrush.MinRadius, TerrainBrush.MaxRadius, 8.0f);
        Strength = AlpineTerrainRules.ClampFinite(Strength, 0.01f, 1.0f, 0.5f);
        Spacing = AlpineTerrainRules.ClampFinite(Spacing, 0.05f, 2.0f, 0.25f);
        float maxY = AlpineTerrainRules.MaxCoord + AlpineTerrainRules.MaxHeightRange;
        SetHeight = AlpineTerrainRules.ClampFinite(SetHeight, -maxY, maxY, 0f);
        RampAngle = AlpineTerrainRules.ClampFinite(RampAngle, -TerrainBrush.MaxRampAngle, TerrainBrush.MaxRampAngle, 15.0f);
        Layer = Math.Max(Layer, 0);
    }

    /// <summary>The mirror these settings give <paramref name="t"/>, its lines kept within the grid (terrain_paint.cpp:356-368).</summary>
    public TerrainMirror? MirrorFor(AlpineTerrain t)
    {
        if (!MirrorX && !MirrorZ)
        {
            return null;
        }

        int cx = t.Grid.CellsX, cz = t.Grid.CellsZ;
        return new TerrainMirror
        {
            X = MirrorX,
            Z = MirrorZ,
            X2 = cx + Math.Clamp(MirrorXOffset2, -cx, cx),
            Z2 = cz + Math.Clamp(MirrorZOffset2, -cz, cz),
        };
    }
}

/// <summary>What the Layer list entry a brush paints is (terrain_paint.cpp:838-853).</summary>
public enum TerrainPaintTarget
{
    BaseLayer,
    Overlay,
    Decoration,
}

/// <summary>
/// One Terrain Tools stroke on a terrain of a document: a press, any number of moves, held-still repeats, and a release
/// that records the stroke as <b>one</b> undo step. Dabs edit a private copy of the grid the terrain shows while the
/// stroke runs (live feedback), one dab per spacing step along the cursor path, exactly as Alpine RED's panel does
/// (editor_patch/terrain_paint.cpp:748-1022): Flatten holds the height where the stroke began, Ramp locks its direction
/// after a short drag, Bridge Points applies once on release, Noise draws a new pattern per stroke and never adds twice
/// at a spot, Geoable Chunks toggles every chunk the cursor crosses to the state the first chunk decided.
/// </summary>
public sealed class TerrainStroke
{
    private readonly EditorDocument _doc;
    private readonly LevelObject _obj;
    private readonly AlpineTerrain _t;
    private readonly TerrainBrushSettings _s;
    private readonly AlpineTerrain _before;
    private readonly TerrainMirror? _mirror;
    private double[]? _exact;
    private float[]? _coverage;
    private bool _hasLast;
    private float _lastX;
    private float _lastZ;
    private bool _hasTarget;
    private float _target;
    private readonly float[] _imageTarget = new float[TerrainBrush.MaxImages];
    private bool _hasAnchor;
    private float _anchorX;
    private float _anchorZ;
    private float _anchorOffset;
    private readonly float[] _anchorOffsets = new float[TerrainBrush.MaxImages];
    private bool _rampLocked;
    private float _rampDirX;
    private float _rampDirZ;
    private bool _hasEnd;
    private float _endX;
    private float _endZ;
    private float _endOffset;
    private readonly float[] _endOffsets = new float[TerrainBrush.MaxImages];
    private readonly uint _seed;
    private float _noiseFeature = 1.0f;
    private bool _geoDecided;
    private bool _geoState;
    private bool _ended;

    /// <summary>Starts a stroke on <paramref name="obj"/>'s terrain with a copy of <paramref name="settings"/>.</summary>
    /// <param name="seed">The Noise pattern; null draws one from the clock as Alpine RED does.</param>
    public TerrainStroke(EditorDocument doc, LevelObject obj, TerrainBrushSettings settings, uint? seed = null)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _obj = obj;
        _t = TerrainOps.Require(obj);
        _s = settings.Clone();
        _s.Clamp();
        if (_t.Layers.Count == 0)
        {
            throw new InvalidOperationException("The terrain has no texture layer to paint.");
        }

        _before = new AlpineTerrain();
        _before.Assign(_t);
        _t.Grid = _t.Grid.Clone(); // the stroke's own copy; the original stays with the undo step
        _mirror = _s.MirrorFor(_t);
        _seed = (uint)(AlpineTerrainRules.SplitMix64(seed ?? (ulong)Environment.TickCount64 ^ 0x5EEDu) | 1u);
    }

    /// <summary>The terrain the stroke edits (showing the stroke so far).</summary>
    public AlpineTerrain Terrain => _t;

    public TerrainBrushSettings Settings => _s;

    /// <summary>True once a dab changed anything.</summary>
    public bool Changed { get; private set; }

    /// <summary>True when the stroke changed heights, holes or diagonals (the compiled geometry is then out of date).</summary>
    public bool EditedGeometry { get; private set; }

    /// <summary>True when the stroke changed weights, overlay or decoration coverage.</summary>
    public bool EditedPaint { get; private set; }

    /// <summary>True when the stroke changed which chunks are geoable.</summary>
    public bool EditedGeoChunks { get; private set; }

    /// <summary>A one-off status note (height range grown, clamped, nothing to paint ...), as Alpine RED's panel shows.</summary>
    public string? Note { get; private set; }

    /// <summary>Chunks Geoable Chunks changed.</summary>
    public int GeoChunkCount { get; private set; }

    /// <summary>Where a Ramp / Bridge Points stroke began, and Bridge Points' far end so far (world X, Z).</summary>
    public (float X, float Z)? Anchor => _hasAnchor ? (_anchorX, _anchorZ) : null;

    public (float X, float Z)? End => _hasEnd ? (_endX, _endZ) : null;

    /// <summary>The world (X, Z) of the last dab.</summary>
    public (float X, float Z)? LastDab => _hasLast ? (_lastX, _lastZ) : null;

    /// <summary>What the selected Layer list entry is, and its index within its own list.</summary>
    public static (TerrainPaintTarget Kind, int Index) ResolveLayer(AlpineTerrain t, int layer)
    {
        int layers = Math.Min(t.Layers.Count, AlpineTerrainRules.MaxLayers);
        if (layer < layers)
        {
            return (TerrainPaintTarget.BaseLayer, Math.Max(layer, 0));
        }

        int overlay = layer - layers;
        int overlays = Math.Min(t.Overlays.Count, AlpineTerrainRules.MaxOverlays);
        if (overlay < overlays)
        {
            return (TerrainPaintTarget.Overlay, overlay);
        }

        int deco = overlay - overlays;
        return deco < t.Decorations.Count ? (TerrainPaintTarget.Decoration, deco) : (TerrainPaintTarget.BaseLayer, 0);
    }

    /// <summary>The surface height at world (x, z) as an offset above the origin (terrain_paint.cpp:341-344).</summary>
    public static float SurfaceOffset(AlpineTerrain t, float x, float z) => TerrainOps.HeightAt(t, x, z) - t.Origin.Y;

    private void ImageSurfaceOffsets(float x, float z, float[] outv)
    {
        outv[0] = SurfaceOffset(_t, x, z);
        for (int k = 1; k < TerrainBrush.MaxImages; k++)
        {
            outv[k] = TerrainBrush.ImageOn(_mirror, k) && _mirror is { } m ? TerrainBrush.MirroredHeightAt(_t, m, k, x, z) - _t.Origin.Y : outv[0];
        }
    }

    /// <summary>
    /// Moves the stroke to world (x, z): the first call dabs there, later ones dab every spacing step along the path
    /// (at most 256 per call). Returns true when the terrain changed.
    /// </summary>
    public bool MoveTo(float x, float z)
    {
        if (_ended || !float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        if (_s.Tool == TerrainTool.GeoableChunks)
        {
            return GeoTo(x, z);
        }

        bool ramp = _s.Tool == TerrainTool.Ramp;
        if ((ramp || _s.Tool == TerrainTool.BridgePoints) && !_hasAnchor)
        {
            _hasAnchor = true;
            _anchorX = x;
            _anchorZ = z;
            _anchorOffset = SurfaceOffset(_t, x, z);
            if (_mirror is not null)
            {
                ImageSurfaceOffsets(x, z, _anchorOffsets);
            }
        }

        if (_s.Tool == TerrainTool.BridgePoints)
        {
            _hasEnd = true;
            _endX = x;
            _endZ = z;
            return false;
        }

        bool changed = false;
        if (ramp && !_rampLocked)
        {
            float dx0 = x - _anchorX, dz0 = z - _anchorZ;
            float dist0 = MathF.Sqrt((dx0 * dx0) + (dz0 * dz0));
            float cs = _t.CellSize;
            if (!(dist0 >= TerrainBrush.RampLockCells(_s.Radius / cs) * cs))
            {
                return false;
            }

            _rampLocked = true;
            _rampDirX = dx0 / dist0;
            _rampDirZ = dz0 / dist0;
            _hasLast = true;
            _lastX = _anchorX;
            _lastZ = _anchorZ;
            changed = ApplyDab(_anchorX, _anchorZ);
        }

        if (!_hasLast)
        {
            _hasLast = true;
            _lastX = x;
            _lastZ = z;
            return ApplyDab(x, z);
        }

        float step = MathF.Max(_s.Spacing * _s.Radius, _t.CellSize * 0.05f);
        float dx = x - _lastX, dz = z - _lastZ;
        float dist = MathF.Sqrt((dx * dx) + (dz * dz));
        if (dist < step)
        {
            return changed;
        }

        dx /= dist;
        dz /= dist;
        for (int n = 0; dist >= step && n < 256; n++)
        {
            _lastX += dx * step;
            _lastZ += dz * step;
            dist -= step;
            changed = ApplyDab(_lastX, _lastZ) || changed;
        }

        return changed;
    }

    /// <summary>The cursor left the terrain: the next move starts a fresh dab path (terrain_paint.cpp:1089).</summary>
    public void Lift() => _hasLast = false;

    /// <summary>Held still: sculpt tools that repeat dab again where they last dabbed (terrain_paint.cpp:1050-1055).</summary>
    public bool Repeat() => !_ended && _hasLast && TerrainBrush.RepeatsInPlace(_s.Tool) && ApplyDab(_lastX, _lastZ);

    /// <summary>
    /// Ends the stroke: Bridge Points applies its ramp now, then everything the stroke changed becomes one undo step.
    /// Returns true when the stroke changed the terrain.
    /// </summary>
    public bool Finish()
    {
        if (_ended)
        {
            return Changed;
        }

        if (_s.Tool == TerrainTool.BridgePoints)
        {
            BridgeApply();
        }

        _ended = true;
        if (!Changed)
        {
            _t.Grid = _before.Grid; // nothing was painted: the terrain keeps its own grid object
            return false;
        }

        var after = new AlpineTerrain();
        after.Assign(_t);
        AlpineTerrain before = _before;
        AlpineTerrain t = _t;
        RflSection section = _obj.Section;
        _doc.Undo.Execute(new RelayCommand(Describe(),
            () =>
            {
                t.Assign(after);
                section.Dirty = true;
            },
            () =>
            {
                t.Assign(before);
                section.Dirty = true;
            }));
        return true;
    }

    /// <summary>Abandons the stroke, restoring the terrain as it was before it; nothing goes on the undo stack.</summary>
    public void Cancel()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        _t.Assign(_before);
        Changed = false;
    }

    private string Describe() => _s.Tool switch
    {
        TerrainTool.PaintLayer => "Paint terrain",
        TerrainTool.EraseToBase => "Erase terrain paint",
        TerrainTool.SmoothWeights => "Smooth terrain paint",
        TerrainTool.PaintHoles => "Paint terrain holes",
        TerrainTool.ClearHoles => "Clear terrain holes",
        TerrainTool.Raise => "Raise terrain",
        TerrainTool.Lower => "Lower terrain",
        TerrainTool.SmoothHeights => "Smooth terrain",
        TerrainTool.Flatten => "Flatten terrain",
        TerrainTool.SetHeight => "Set terrain height",
        TerrainTool.Noise => "Terrain noise",
        TerrainTool.GeoableChunks => "Geoable terrain chunks",
        TerrainTool.Ramp => "Terrain ramp",
        TerrainTool.BridgePoints => "Bridge terrain points",
        _ => "Terrain stroke",
    };

    private bool BridgeApply()
    {
        if (!_hasAnchor || !_hasEnd)
        {
            return false;
        }

        float cs = _t.CellSize;
        float dx = (_endX - _anchorX) / cs, dz = (_endZ - _anchorZ) / cs;
        if (!(MathF.Sqrt((dx * dx) + (dz * dz)) >= TerrainBrush.MinSegmentCells))
        {
            Note = "Bridge Points: drag from one end to the other";
            return false;
        }

        _endOffset = SurfaceOffset(_t, _endX, _endZ);
        if (_mirror is not null)
        {
            ImageSurfaceOffsets(_endX, _endZ, _endOffsets);
        }

        return ApplyDab(_anchorX, _anchorZ);
    }

    private bool GeoTo(float x, float z)
    {
        if (!_t.Geoable)
        {
            Note = "Geoable Chunks needs a geoable terrain";
            return false;
        }

        AlpineTerrainGrid g = _t.Grid;
        int edge = Compiler.TerrainChunkEmitter.EffectiveChunkCells(_t);
        int cellsX = g.CellsX, cellsZ = g.CellsZ;
        int count = AlpineTerrainRules.ChunkCount(cellsX, cellsZ, edge);
        float cx = (x - _t.Origin.X) / _t.CellSize, cz = (z - _t.Origin.Z) / _t.CellSize;
        float px = _hasLast ? (_lastX - _t.Origin.X) / _t.CellSize : cx;
        float pz = _hasLast ? (_lastZ - _t.Origin.Z) / _t.CellSize : cz;
        _hasLast = true;
        _lastX = x;
        _lastZ = z;
        if (!_geoDecided)
        {
            _geoDecided = true;
            _geoState = !ChunkGeoable(g, TerrainBrush.ChunkAt(cellsX, cellsZ, edge, cx, cz));
        }

        bool changed = false;
        void Visit(int k)
        {
            if (ChunkGeoable(g, k) == _geoState)
            {
                return;
            }

            if (g.GeoMask is null || g.GeoMask.Length != AlpineTerrainRules.ChunkMaskBytes(count))
            {
                var full = new byte[AlpineTerrainRules.ChunkMaskBytes(count)];
                for (int i = 0; i < count; i++)
                {
                    AlpineTerrainRules.SetBit(full, i, true);
                }

                g.GeoMask = full;
            }

            AlpineTerrainRules.SetBit(g.GeoMask, k, _geoState);
            GeoChunkCount++;
            changed = true;
        }

        for (int k = 0; k < TerrainBrush.MaxImages; k++)
        {
            if (!TerrainBrush.ImageOn(_mirror, k))
            {
                continue;
            }

            float ax = px, az = pz, bx = cx, bz = cz;
            if ((k & 1) != 0 && _mirror is { } m1)
            {
                ax = m1.X2 - ax;
                bx = m1.X2 - bx;
            }

            if ((k & 2) != 0 && _mirror is { } m2)
            {
                az = m2.Z2 - az;
                bz = m2.Z2 - bz;
            }

            if (_mirror is not null && !TerrainBrush.ClipSegment(ref ax, ref az, ref bx, ref bz, cellsX, cellsZ))
            {
                continue;
            }

            TerrainBrush.ChunksOnSegment(cellsX, cellsZ, edge, ax, az, bx, bz, Visit);
        }

        if (changed)
        {
            // A mask that marks every chunk is stored as none (the writer omits it; terrain.cpp:435-452).
            if (g.GeoMask is { } mask && Enumerable.Range(0, count).All(i => AlpineTerrainRules.GetBit(mask, i)))
            {
                g.GeoMask = null;
            }

            Changed = true;
            EditedGeoChunks = true;
            Note = $"{GeoChunkCount} chunk(s) now {(_geoState ? "geoable" : "not geoable")}";
        }

        return changed;
    }

    /// <summary>Whether chunk <paramref name="k"/> is geoable: every chunk is when the terrain stores no mask.</summary>
    public static bool ChunkGeoable(AlpineTerrainGrid g, int k) => g.GeoMask is not { } m || (k >> 3) >= m.Length || AlpineTerrainRules.GetBit(m, k);

    private bool ApplyDab(float x, float z)
    {
        AlpineTerrainGrid g = _t.Grid;
        var dab = new TerrainDab
        {
            Cx = (x - _t.Origin.X) / _t.CellSize,
            Cz = (z - _t.Origin.Z) / _t.CellSize,
            Radius = MathF.Max(_s.Radius / _t.CellSize, 0.01f),
            Strength = _s.Strength,
            Falloff = _s.Falloff,
        };
        if (TerrainBrush.EditsHeights(_s.Tool))
        {
            return SculptDab(g, dab, x, z);
        }

        bool changed = false;
        TerrainRect r = default;
        (TerrainPaintTarget kind, int index) = ResolveLayer(_t, _s.Layer);
        int plane = AlpineTerrainRules.WeightTexelCount(g.Nx, g.Nz, g.WeightResolution);
        switch (_s.Tool)
        {
            case TerrainTool.PaintLayer:
            case TerrainTool.EraseToBase:
            case TerrainTool.SmoothWeights:
                bool raise = _s.Tool == TerrainTool.PaintLayer;
                if (kind == TerrainPaintTarget.Overlay && g.OverlayCoverage is { } cov)
                {
                    r = _s.Tool == TerrainTool.SmoothWeights
                        ? TerrainBrush.SmoothCoverage(cov, 0, 4, index, g, dab, _mirror)
                        : TerrainBrush.PaintCoverage(cov, 0, 4, index, g, dab, raise, _mirror);
                }
                else if (kind == TerrainPaintTarget.Decoration && g.DecorationCoverage is { } planes && planes.Length >= (index + 1) * plane)
                {
                    r = _s.Tool == TerrainTool.SmoothWeights
                        ? TerrainBrush.SmoothCoverage(planes, index * plane, 1, 0, g, dab, _mirror)
                        : TerrainBrush.PaintCoverage(planes, index * plane, 1, 0, g, dab, raise, _mirror);
                }
                else if (kind == TerrainPaintTarget.BaseLayer)
                {
                    int layers = Math.Min(_t.Layers.Count, AlpineTerrainRules.MaxLayers);
                    r = _s.Tool switch
                    {
                        TerrainTool.PaintLayer => TerrainBrush.PaintLayer(g, layers, dab, index, _mirror),
                        TerrainTool.EraseToBase => TerrainBrush.PaintLayer(g, layers, dab, 0, _mirror),
                        _ => TerrainBrush.SmoothWeights(g, dab, _mirror),
                    };
                }

                if (!r.IsEmpty)
                {
                    EditedPaint = true;
                }

                break;
            case TerrainTool.PaintHoles:
            case TerrainTool.ClearHoles:
                r = TerrainBrush.PaintHoles(g.Holes, g.CellsX, g.CellsZ, dab, _s.Tool == TerrainTool.PaintHoles, _mirror);
                if (!r.IsEmpty)
                {
                    EditedGeometry = true;
                }

                break;
        }

        changed = !r.IsEmpty;
        Changed |= changed;
        return changed;
    }

    private bool SculptDab(AlpineTerrainGrid g, in TerrainDab dab, float x, float z)
    {
        var s = new TerrainBrush.SculptDab { Tool = _s.Tool, Dab = dab, Mirror = _mirror };
        switch (_s.Tool)
        {
            case TerrainTool.Raise:
            case TerrainTool.Lower:
                s.Amount = TerrainBrush.RaiseStep(_s.Radius, _s.Strength);
                break;
            case TerrainTool.Flatten:
                if (!_hasTarget)
                {
                    _hasTarget = true;
                    _target = SurfaceOffset(_t, x, z);
                    if (_mirror is not null)
                    {
                        ImageSurfaceOffsets(x, z, _imageTarget);
                    }
                }

                s.Target = _target;
                Array.Copy(_imageTarget, s.ImageTarget, TerrainBrush.MaxImages);
                break;
            case TerrainTool.SetHeight:
                s.Target = _s.SetHeight - _t.Origin.Y;
                break;
            case TerrainTool.Ramp:
                if (!_rampLocked)
                {
                    return false;
                }

                s.Target = _anchorOffset;
                Array.Copy(_anchorOffsets, s.ImageTarget, TerrainBrush.MaxImages);
                s.PlaneX = (_anchorX - _t.Origin.X) / _t.CellSize;
                s.PlaneZ = (_anchorZ - _t.Origin.Z) / _t.CellSize;
                (float sx, float sz) = TerrainBrush.RampSlope(_s.RampAngle, _rampDirX, _rampDirZ, _t.CellSize);
                s.SlopeX = sx;
                s.SlopeZ = sz;
                break;
            case TerrainTool.BridgePoints:
                if (!_hasAnchor || !_hasEnd)
                {
                    return false;
                }

                s.Target = _anchorOffset;
                s.TargetEnd = _endOffset;
                Array.Copy(_anchorOffsets, s.ImageTarget, TerrainBrush.MaxImages);
                Array.Copy(_endOffsets, s.ImageTargetEnd, TerrainBrush.MaxImages);
                s.SegX = (_endX - _anchorX) / _t.CellSize;
                s.SegZ = (_endZ - _anchorZ) / _t.CellSize;
                break;
            case TerrainTool.Noise:
                int cover = g.Heights.Length * (_mirror is not null ? TerrainBrush.MaxImages : 1);
                if (_coverage is null || _coverage.Length != cover)
                {
                    _coverage = new float[cover];
                    _noiseFeature = TerrainBrush.NoiseFeatureCells(dab.Radius);
                }

                s.Amount = TerrainBrush.NoiseAmplitude(_s.Radius, _s.Strength);
                s.Base = _before.Grid.Heights;
                s.BaseMin = _before.HeightMin;
                s.BaseRange = _before.HeightRange;
                s.Coverage = _coverage;
                s.Seed = _seed;
                s.FeatureCells = _noiseFeature;
                break;
        }

        if (_exact is null || _exact.Length != g.Heights.Length)
        {
            _exact = new double[g.Heights.Length];
            Array.Fill(_exact, double.NaN);
        }

        s.Exact = _exact;
        var hg = new TerrainBrush.HeightGrid
        {
            Heights = g.Heights,
            Nx = g.Nx,
            Nz = g.Nz,
            HeightMin = _t.HeightMin,
            HeightRange = _t.HeightRange,
            HeadroomFloor = _t.Geoable ? (double)_t.HeightMin - _t.Thickness + AlpineTerrainRules.MinThickness : double.NegativeInfinity,
        };
        TerrainBrush.HeightWrite w = TerrainBrush.Sculpt(hg, s);
        if (w.Requantized)
        {
            _t.Thickness = TerrainOps.ThicknessAfterGrowth(_t.Flags, _t.Thickness, _t.HeightMin, hg.HeightMin);
            _t.HeightMin = hg.HeightMin;
            _t.HeightRange = hg.HeightRange;
            Note = $"Height range grown to {hg.HeightMin:0.######} .. {(double)hg.HeightMin + hg.HeightRange:0.######} above the origin";
        }

        if (w.Clamped)
        {
            Note = $"Heights clamped: the height range is at its limit ({AlpineTerrainRules.MaxHeightRange} span, min {-AlpineTerrainRules.MaxCoord})";
        }

        if (_mirror is { } m)
        {
            for (int k = 0; k < TerrainBrush.MaxImages; k++)
            {
                TerrainBrush.MirrorDiagonals(g.Diagonals, g.CellsX, g.CellsZ, m, dab.Cx, dab.Cz, w.ImageVerts[k]);
            }
        }

        bool changed = !w.Verts.IsEmpty;
        if (changed)
        {
            Changed = true;
            EditedGeometry = true;
        }

        return changed;
    }
}

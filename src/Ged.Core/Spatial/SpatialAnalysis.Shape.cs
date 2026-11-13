using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Spatial;

// Shape (walls, ceilings, verticality), bridges, population per cell and texturing statistics.
public sealed partial class SpatialAnalysis
{
    private List<(Face F, Vec3d[] Poly, Vec3d N, double A)>? _visibleFaces;

    /// <summary>The visible compiled faces: not portals, liquid surfaces, sky faces, sky-room faces or invisible helpers.</summary>
    private List<(Face F, Vec3d[] Poly, Vec3d N, double A)> VisibleFaces()
    {
        if (_visibleFaces is not null)
        {
            return _visibleFaces;
        }

        Geometry geo = _level.Geometry;
        var list = new List<(Face, Vec3d[], Vec3d, double)>();
        for (int fi = 0; fi < geo.Faces.Count; fi++)
        {
            Face f = geo.Faces[fi];
            if (f.IsPortalFace || f.Vertices.Count < 3 || (f.Flags & (ushort)(FaceFlags.LiquidSurface | FaceFlags.ShowSky)) != 0)
            {
                continue;
            }

            if (f.RoomIndex >= 0 && f.RoomIndex < geo.Rooms.Count && geo.Rooms[f.RoomIndex].IsSkyroom != 0)
            {
                continue;
            }

            if (SpaceSampler.IsInvisibleTexture(geo, f) || SpaceSampler.Polygon(geo, f) is not { } pg)
            {
                continue;
            }

            list.Add((f, pg.Poly, pg.Normal, pg.Area));
        }

        _visibleFaces = list;
        return list;
    }

    // ---- shape -----------------------------------------------------------------------------------

    private JsonObject? _shapeReport;

    /// <summary>The 8 m cell-node pairs whose median ceilings differ by ≥ 1.5 m (ceiling variety), as (a, b).</summary>
    internal List<(int A, int B)> CeilingChanges { get; } = new();

    private JsonObject ShapeReport()
    {
        if (_shapeReport is not null)
        {
            return _shapeReport;
        }

        double wallA = 0, offAxisA = 0, slopeA = 0, totalA = 0, floorA = 0, slopedFloorA = 0;
        var wallPlanes = new HashSet<(int, int, int)>();
        foreach ((Face _, Vec3d[] poly, Vec3d n, double a) in VisibleFaces())
        {
            totalA += a;
            double ay = Math.Abs(n.Y);
            if (ay < 0.3)
            {
                wallA += a;
                double ang = Math.Atan2(n.Z, n.X) * 180 / Math.PI;
                double m = ((ang % 90) + 90) % 90;
                double off = Math.Min(m, 90 - m);
                if (off > 5)
                {
                    offAxisA += a;
                }

                double dpl = -((n.X * poly[0].X) + (n.Y * poly[0].Y) + (n.Z * poly[0].Z));
                wallPlanes.Add(((int)Math.Round(ang / 2), (int)Math.Round(dpl * 4), 0));
            }
            else if (ay < 0.97)
            {
                slopeA += a;
                if (n.Y > 0)
                {
                    slopedFloorA += a;
                }
            }
            else if (n.Y > 0)
            {
                floorA += a;
            }
        }

        List<int> play = _w.PlayIdx;
        List<double> indoor = play.Where(i => !_s[i].OpenSky).Select(i => _s[i].Headroom).ToList();
        int hrBins = indoor.GroupBy(h => (int)Math.Floor(h)).Count(gp => gp.Count() >= 0.03 * Math.Max(1, indoor.Count));
        int floorBins = play.GroupBy(i => (int)Math.Floor(_s[i].P.Y / 0.5)).Count(gp => gp.Count() >= 0.02 * play.Count);
        double[] ys = play.Select(i => _s[i].P.Y).OrderBy(y => y).ToArray();
        double y0 = Percentiles.Type7(ys, 0.01), y1 = Percentiles.Type7(ys, 0.99);
        CellGraph cg = Cell8;
        int changes = 0, pairs = 0;
        for (int u = 0; u < cg.N; u++)
        {
            foreach (int v in cg.Graph.Adj[u])
            {
                if (v <= u || double.IsNaN(cg.Headroom[u]) || double.IsNaN(cg.Headroom[v]))
                {
                    continue;
                }

                pairs++;
                if (Math.Abs(cg.Headroom[u] - cg.Headroom[v]) >= 1.5)
                {
                    changes++;
                    CeilingChanges.Add((u, v));
                }
            }
        }

        double? iqr = null;
        if (indoor.Count > 4)
        {
            double[] sorted = indoor.OrderBy(x => x).ToArray();
            iqr = R((Percentiles.Type7(sorted, 0.75) - Percentiles.Type7(sorted, 0.25)) / Med(indoor));
        }

        _shapeReport = new JsonObject
        {
            ["wall_offaxis_fraction"] = R(offAxisA / Math.Max(1e-9, wallA)),
            ["sloped_area_fraction"] = R(slopeA / Math.Max(1e-9, totalA)),
            ["sloped_floor_fraction"] = R(slopedFloorA / Math.Max(1e-9, slopedFloorA + floorA)),
            ["wall_planes_per_100m2"] = R(wallPlanes.Count / (_w.PlayArea / 100)),
            ["headroom_indoor"] = Dist(indoor),
            ["headroom_iqr_over_median"] = iqr,
            ["headroom_classes"] = hrBins,
            ["open_sky_fraction"] = R(play.Count(i => _s[i].OpenSky) / (double)play.Count),
            ["outdoor_fraction"] = R(OutdoorFraction()),
            ["floor_height_classes"] = floorBins,
            ["vertical_span_p1_p99"] = R(y1 - y0),
            ["ceiling_change_edge_fraction"] = pairs > 0 ? R(changes / (double)pairs) : null,
            ["clear_width"] = Dist(play.Select(i => _s[i].Width)),
            ["corridor_fraction"] = R(play.Count(i => _s[i].Corridor) / (double)play.Count),
        };
        return _shapeReport;
    }

    private bool[]? _skyCeiling;

    /// <summary>Per sample: under open sky, or under a sky face (computed for every other lattice sample, by parity).</summary>
    private bool SkyAbove(int i)
    {
        if (_s[i].OpenSky)
        {
            return true;
        }

        if (_sp.Cast(_s[i].P.Add(new Vec3d(0, 0.01, 0)), new Vec3d(0, 1, 0), 200, SpaceSampler.HitFilter.DownFacing, out int fi) is not null && fi >= 0)
        {
            return (_level.Geometry.Faces[_sp.Faces[fi].Source].Flags & (ushort)FaceFlags.ShowSky) != 0;
        }

        return false;
    }

    private double OutdoorFraction()
    {
        List<int> even = _w.PlayIdx.Where(i => (_s[i].I + _s[i].K) % 2 == 0).ToList();
        _skyCeiling = new bool[_s.Count];
        var flags = new bool[even.Count];
        For(even.Count, j => flags[j] = SkyAbove(even[j]));
        int sky = 0;
        for (int j = 0; j < even.Count; j++)
        {
            if (flags[j])
            {
                _skyCeiling[even[j]] = true;
                sky++;
            }
        }

        return even.Count > 0 ? sky / (double)even.Count : 0;
    }

    // ---- bridges -----------------------------------------------------------------------------------

    private JsonObject? _bridgesReport;

    /// <summary>Bridge / elevated-walkway pieces (≥ 8 m² of floor with ≥ 3 m drops on both sides): area, length, width, drop, height, centre.</summary>
    internal List<(double Area, double Len, double Wid, double Drop, double Y, Vec3d Centre)> BridgePieces { get; } = new();

    private JsonObject BridgesReport()
    {
        if (_bridgesReport is not null)
        {
            return _bridgesReport;
        }

        List<int> play = _w.PlayIdx;
        var dropOf = new double[play.Count];
        For(play.Count, j =>
        {
            SpaceSampler.Sample s = _s[play[j]];
            Vec3d ax = SpaceSampler.Directions[s.Axis];
            var perp = new Vec3d(-ax.Z, 0, ax.X);
            double dl = SideDrop(s.P, perp), dr = SideDrop(s.P, perp.Scale(-1));
            dropOf[j] = dl >= 3 && dr >= 3 ? Math.Min(dl, dr) : -1;
        });
        var isB = new bool[_s.Count];
        var drops = new Dictionary<int, double>();
        for (int j = 0; j < play.Count; j++)
        {
            if (dropOf[j] >= 0)
            {
                isB[play[j]] = true;
                drops[play[j]] = dropOf[j];
            }
        }

        var seen = new bool[_s.Count];
        var comps = new List<(double Area, double Len, double Wid, double Drop, double Y, Vec3d C)>();
        foreach (int i in play)
        {
            if (!isB[i] || seen[i])
            {
                continue;
            }

            var comp = new List<int>();
            var q = new Queue<int>();
            q.Enqueue(i);
            seen[i] = true;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                comp.Add(u);
                foreach (WalkEdge e in _w.Adj[u])
                {
                    if (isB[e.To] && !seen[e.To] && !e.Drop && !e.Link && Math.Abs(_s[e.To].P.Y - _s[u].P.Y) < 0.6)
                    {
                        seen[e.To] = true;
                        q.Enqueue(e.To);
                    }
                }
            }

            double area = comp.Count * _g * _g;
            if (area < 8)
            {
                continue;
            }

            (double len, double _) = PcaExtent(comp.Select(c => _s[c].P));
            var centre = new Vec3d(comp.Average(c => _s[c].P.X), comp.Average(c => _s[c].P.Y), comp.Average(c => _s[c].P.Z));
            comps.Add((area, len, area / Math.Max(len, 1e-6), Med(comp.Select(c => drops[c])), Med(comp.Select(c => _s[c].P.Y)), centre));
        }

        var list = new JsonArray();
        foreach (var c in comps.OrderByDescending(c => c.Area).Take(6))
        {
            list.Add(new JsonObject { ["area"] = R(c.Area), ["length"] = R(c.Len), ["mean_width"] = R(c.Wid), ["drop"] = R(c.Drop), ["y"] = R(c.Y) });
        }

        BridgePieces.AddRange(comps.OrderByDescending(c => c.Area).Select(c => (c.Area, c.Len, c.Wid, c.Drop, c.Y, c.C)));
        _bridgesReport = new JsonObject
        {
            ["count"] = comps.Count,
            ["area_fraction"] = R(comps.Sum(c => c.Area) / _w.PlayArea),
            ["largest"] = list,
        };
        return _bridgesReport;
    }

    /// <summary>The drop beside a floor point within 1–4 m in <paramref name="dir"/> (≥ 3 m), or 0 when a wall or a small step comes first.</summary>
    private double SideDrop(Vec3d p, Vec3d dir)
    {
        for (double k = 1.0; k <= 4.01; k += 0.5)
        {
            Vec3d q = p.Add(dir.Scale(k));
            var o = new Vec3d(p.X, p.Y + 0.5, p.Z);
            if (_sp.Blocked(o, dir, k, SpaceSampler.HitFilter.Any))
            {
                return 0; // a wall before the drop
            }

            double? down = _sp.Cast(new Vec3d(q.X, p.Y + 0.5, q.Z), new Vec3d(0, -1, 0), 60, SpaceSampler.HitFilter.UpFacing);
            double drop = (down ?? 60) - 0.5;
            if (drop >= 3)
            {
                return drop;
            }

            if (drop > 0.6)
            {
                return 0;
            }
        }

        return 0;
    }

    // ---- population ------------------------------------------------------------------------------

    private JsonObject? _populationReport;

    /// <summary>Per 8 m cell node: dressing elements counted in it and distinct wall planes seen from it (8 rays at 1 m).</summary>
    internal int[] NodeDressing { get; private set; } = Array.Empty<int>();

    internal int[] NodePlanes { get; private set; } = Array.Empty<int>();

    private static (Vec3d Min, Vec3d Max, Vec3d LocalExt) BrushBox(Brush b)
    {
        Mat3 r = b.Rotation;
        Vec3d pos = SpatialObjects.V(b.Position);
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        double lx0 = double.MaxValue, ly0 = double.MaxValue, lz0 = double.MaxValue, lx1 = double.MinValue, ly1 = double.MinValue, lz1 = double.MinValue;
        foreach (Vec3 v in b.Geometry.Vertices)
        {
            lx0 = Math.Min(lx0, v.X);
            lx1 = Math.Max(lx1, v.X);
            ly0 = Math.Min(ly0, v.Y);
            ly1 = Math.Max(ly1, v.Y);
            lz0 = Math.Min(lz0, v.Z);
            lz1 = Math.Max(lz1, v.Z);
            Vec3d w = pos.Add(SpatialObjects.V(r.Right).Scale(v.X)).Add(SpatialObjects.V(r.Up).Scale(v.Y)).Add(SpatialObjects.V(r.Forward).Scale(v.Z));
            x0 = Math.Min(x0, w.X);
            x1 = Math.Max(x1, w.X);
            y0 = Math.Min(y0, w.Y);
            y1 = Math.Max(y1, w.Y);
            z0 = Math.Min(z0, w.Z);
            z1 = Math.Max(z1, w.Z);
        }

        return (new Vec3d(x0, y0, z0), new Vec3d(x1, y1, z1), new Vec3d(lx1 - lx0, ly1 - ly0, lz1 - lz0));
    }

    private JsonObject PopulationReport()
    {
        if (_populationReport is not null)
        {
            return _populationReport;
        }

        CellGraph cg = Cell8;
        IReadOnlyList<Brush> brushes = _level.Brushes();
        int air = 0, solid = 0, detail = 0, beams = 0, slabs = 0, smallSolid = 0, rotated = 0;
        var dressPts = new List<(Vec3d P, string Kind)>();
        foreach (Brush b in brushes)
        {
            if (b.Geometry.Vertices.Count == 0)
            {
                continue;
            }

            if ((b.Flags & (uint)BrushFlags.Air) != 0)
            {
                air++;
                continue;
            }

            solid++;
            (Vec3d mn, Vec3d mx, Vec3d le) = BrushBox(b);
            double[] e = new[] { le.X, le.Y, le.Z }.OrderBy(x => x).ToArray();
            Vec3d c = mn.Add(mx).Scale(0.5);
            bool isDetail = (b.Flags & (uint)BrushFlags.Detail) != 0;
            float[] rm = { b.Rotation.Right.X, b.Rotation.Right.Y, b.Rotation.Right.Z, b.Rotation.Up.X, b.Rotation.Up.Y, b.Rotation.Up.Z, b.Rotation.Forward.X, b.Rotation.Forward.Y, b.Rotation.Forward.Z };
            if (rm.Any(x => Math.Min(Math.Abs(x), Math.Abs(Math.Abs(x) - 1)) > 1e-3))
            {
                rotated++; // off the 90-degree lattice
            }

            string? kind = null;
            if (e[0] <= 0.75 && e[1] <= 0.75 && e[2] >= 2)
            {
                beams++;
                kind = "beam";
            }
            else if (e[0] <= 0.5 && e[1] > 0.75 && e[2] <= 16)
            {
                slabs++;
                kind = "slab";
            }
            else if (e[2] <= 4 && e[0] * e[1] * e[2] <= 16)
            {
                smallSolid++;
                kind = "small";
            }

            if (isDetail)
            {
                detail++;
                kind ??= "detail";
            }

            if (kind is not null && e[2] <= 24)
            {
                dressPts.Add((c, kind));
            }
        }

        foreach (var d in _obj.Dress)
        {
            dressPts.Add((d.P, d.Kind));
        }

        var nodeDress = new int[cg.N];
        foreach (var d in dressPts)
        {
            int n = cg.NodeAt(d.P, 1.5, 9);
            if (n >= 0)
            {
                nodeDress[n]++;
            }
        }

        // Distinct planes seen from each node (every other sample, 8 rays at 1 m).
        List<int> even = _w.PlayIdx.Where(i => (_s[i].I + _s[i].K) % 2 == 0 && cg.NodeOfSample[i] >= 0).ToList();
        var hitPlanes = new (int, int, int, int)?[even.Count * 8];
        For(even.Count, j =>
        {
            Vec3d o = _s[even[j]].P.Add(new Vec3d(0, 1.0, 0));
            for (int k = 0; k < 16; k += 2)
            {
                if (_sp.Cast(o, SpaceSampler.Directions[k], 10, SpaceSampler.HitFilter.Any, out int fi) is not null && fi >= 0)
                {
                    SpaceSampler.SFace f = _sp.Faces[fi];
                    hitPlanes[(j * 8) + (k / 2)] = ((int)Math.Round(f.N.X * 20), (int)Math.Round(f.N.Y * 20), (int)Math.Round(f.N.Z * 20), (int)Math.Round(f.D * 2));
                }
            }
        });
        var nodePlanes = new HashSet<(int, int, int, int)>[cg.N];
        for (int n = 0; n < cg.N; n++)
        {
            nodePlanes[n] = new HashSet<(int, int, int, int)>();
        }

        for (int j = 0; j < even.Count; j++)
        {
            int n = cg.NodeOfSample[even[j]];
            for (int k = 0; k < 8; k++)
            {
                if (hitPlanes[(j * 8) + k] is { } pl)
                {
                    nodePlanes[n].Add(pl);
                }
            }
        }

        NodeDressing = nodeDress;
        NodePlanes = nodePlanes.Select(p => p.Count).ToArray();
        double bareA = 0, dressedA = 0, plainA = 0;
        var perNodeDensity = new List<double>();
        for (int n = 0; n < cg.N; n++)
        {
            double a = cg.Area[n];
            if (a < 8)
            {
                continue;
            }

            if (nodeDress[n] == 0 && nodePlanes[n].Count <= 5)
            {
                bareA += a;
            }

            if (nodeDress[n] > 0)
            {
                dressedA += a;
            }

            if (nodePlanes[n].Count <= 5)
            {
                plainA += a;
            }

            perNodeDensity.Add(nodeDress[n] / a * 100);
        }

        double tot = Enumerable.Range(0, cg.N).Where(n => cg.Area[n] >= 8).Sum(n => cg.Area[n]);
        double per100 = 100 / _w.PlayArea;
        var cc = new JsonObject();
        foreach (var kv in _obj.Dress.Where(d => d.Kind == "clutter").GroupBy(d => SpatialObjects.ClutterCategory(d.Cls)).Select(g => (g.Key, Count: g.Count())).OrderByDescending(k => k.Count))
        {
            cc[kv.Key] = kv.Count;
        }

        _populationReport = new JsonObject
        {
            ["brushes_solid"] = solid, ["brushes_air"] = air, ["brushes_detail"] = detail,
            ["beams"] = beams, ["slabs"] = slabs, ["small_solids"] = smallSolid, ["rotated_solids"] = rotated,
            ["solid_per100"] = R(solid * per100), ["detail_per100"] = R(detail * per100),
            ["beams_per100"] = R(beams * per100), ["slabs_per100"] = R(slabs * per100), ["small_solids_per100"] = R(smallSolid * per100),
            ["rotated_solid_fraction"] = R(rotated / (double)Math.Max(1, solid)),
            ["clutter_per100"] = R(_obj.Dress.Count(d => d.Kind == "clutter") * per100),
            ["decals_per100"] = R(_obj.Dress.Count(d => d.Kind == "decal") * per100),
            ["meshes_per100"] = R(_obj.Dress.Count(d => d.Kind == "mesh") * per100),
            ["emitters_per100"] = R(_obj.Dress.Count(d => d.Kind == "emitter") * per100),
            ["lights_per100"] = R(_obj.Lights * per100),
            ["dressing_per100"] = R(dressPts.Count * per100),
            ["clutter_categories"] = cc,
            ["cell_dressing_density_per100"] = Dist(perNodeDensity),
            ["bare_area_fraction"] = R(bareA / Math.Max(1, tot)),
            ["undressed_area_fraction"] = R(1 - (dressedA / Math.Max(1, tot))),
            ["plain_geometry_area_fraction"] = R(plainA / Math.Max(1, tot)),
            ["cell_planes"] = Dist(Enumerable.Range(0, cg.N).Where(n => cg.Area[n] >= 8).Select(n => (double)nodePlanes[n].Count)),
        };
        return _populationReport;
    }

    // ---- texturing -------------------------------------------------------------------------------

    private JsonObject? _texturingReport;

    private string TexName(Face f) => f.Texture >= 0 && f.Texture < _level.Geometry.Textures.Count ? _level.Geometry.Textures[f.Texture].ToLowerInvariant() : string.Empty;

    private (int W, int H)? TexSize(string name) => _level.TextureSize?.Invoke(name);

    private JsonObject TexturingReport()
    {
        if (_texturingReport is not null)
        {
            return _texturingReport;
        }

        Geometry geo = _level.Geometry;
        CellGraph cg = Cell8;
        var area = new Dictionary<string, double>[3];
        for (int r = 0; r < 3; r++)
        {
            area[r] = new Dictionary<string, double>();
        }

        double total = 0, offRot = 0, aniso = 0, skew = 0, edgeAligned = 0, uvKnown = 0, detailA = 0, scrollA = 0, trimA = 0;
        int trimN = 0;
        var ppm = new List<(double V, double A)>();
        var mpt = new List<(string T, double V, double A)>();
        List<(Face F, Vec3d[] Poly, Vec3d N, double A)> faceList = VisibleFaces();
        var edges = new Dictionary<(int, int), List<(int Face, int Va, int Vb)>>();
        for (int idx = 0; idx < faceList.Count; idx++)
        {
            Face f = faceList[idx].F;
            for (int k = 0; k < f.Vertices.Count; k++)
            {
                int a = f.Vertices[k].Index, b = f.Vertices[(k + 1) % f.Vertices.Count].Index;
                (int, int) key = a < b ? (a, b) : (b, a);
                if (!edges.TryGetValue(key, out var l))
                {
                    edges[key] = l = new List<(int, int, int)>();
                }

                l.Add((idx, k, (k + 1) % f.Vertices.Count));
            }
        }

        for (int idx = 0; idx < faceList.Count; idx++)
        {
            (Face f, Vec3d[] poly, Vec3d n, double a) = faceList[idx];
            total += a;
            string t = TexName(f);
            int role = n.Y >= 0.7 ? 0 : n.Y <= -0.7 ? 2 : 1;
            area[role][t] = area[role].GetValueOrDefault(t) + a;
            if ((f.Flags & (ushort)FaceFlags.IsDetail) != 0)
            {
                detailA += a;
            }

            if ((f.Flags & (ushort)FaceFlags.ScrollTexture) != 0)
            {
                scrollA += a;
            }

            if (UvGrad(poly, f, n) is not { } gr)
            {
                continue;
            }

            uvKnown += a;
            (Vec3d gu, Vec3d gv) = gr;
            double lu = gu.Length, lv = gv.Length;
            if (lu < 1e-6 || lv < 1e-6)
            {
                continue;
            }

            Vec3d h = Math.Abs(n.Y) < 0.7 ? new Vec3d(-n.Z, 0, n.X).Normalized() : new Vec3d(1, 0, 0).Sub(n.Scale(n.X)).Normalized();
            double ang = Math.Atan2(h.Cross(gu).Dot(n), h.Dot(gu)) * 180 / Math.PI;
            double m = ((ang % 90) + 90) % 90;
            if (Math.Min(m, 90 - m) > 2)
            {
                offRot += a;
            }

            double pu = lu, pv = lv;
            if (TexSize(t) is { } whA)
            {
                pu = lu * whA.W;
                pv = lv * whA.H;
            }

            if (Math.Abs((pu / pv) - 1) > 0.1)
            {
                aniso += a;
            }

            double cos = Math.Abs(gu.Dot(gv)) / (lu * lv);
            if (cos > Math.Sin(2 * Math.PI / 180))
            {
                skew += a;
            }

            mpt.Add((t, 1 / Math.Sqrt(lu * lv), a));
            if (TexSize(t) is { } wh)
            {
                ppm.Add((Math.Sqrt(wh.W * lu * wh.H * lv), a));
            }

            // Edge alignment: an edge (≥ 0.5 m) along which u or v is constant and sits on a quarter-tile line.
            bool al = false;
            for (int k = 0; k < poly.Length && !al; k++)
            {
                Vec3d pa = poly[k];
                Vec3d pb = poly[(k + 1) % poly.Length];
                Vec3d d = pb.Sub(pa);
                if (d.Length < 0.5)
                {
                    continue;
                }

                Uv uva = f.Vertices[k].TextureCoords;
                foreach ((Vec3d gg, double val) in new[] { (gu, (double)uva.U), (gv, (double)uva.V) })
                {
                    if (Math.Abs(gg.Dot(d)) < 0.004)
                    {
                        double q = val * 4;
                        if (Math.Abs(q - Math.Round(q)) < 0.04)
                        {
                            al = true;
                        }
                    }
                }
            }

            if (al)
            {
                edgeAligned += a;
            }
        }

        // UV continuity across shared edges, and trim strips.
        double contCo = 0, totCo = 0, contCorner = 0, totCorner = 0;
        var nbTex = new Dictionary<int, HashSet<string>>();
        foreach (((int, int) key, var l) in edges)
        {
            if (l.Count != 2)
            {
                continue;
            }

            var fa = faceList[l[0].Face];
            var fb = faceList[l[1].Face];
            string ta = TexName(fa.F), tb = TexName(fb.F);
            if (!nbTex.TryGetValue(l[0].Face, out HashSet<string>? sa))
            {
                nbTex[l[0].Face] = sa = new HashSet<string>();
            }

            if (!nbTex.TryGetValue(l[1].Face, out HashSet<string>? sb))
            {
                nbTex[l[1].Face] = sb = new HashSet<string>();
            }

            sa.Add(tb);
            sb.Add(ta);
            if (ta != tb)
            {
                continue;
            }

            double len = SpatialObjects.V(geo.Vertices[key.Item1]).Sub(SpatialObjects.V(geo.Vertices[key.Item2])).Length;
            if (len < 0.25)
            {
                continue;
            }

            static Uv UvOf(Face f, int vi) => f.Vertices.First(v => v.Index == vi).TextureCoords;
            Uv a1 = UvOf(fa.F, key.Item1), a2 = UvOf(fa.F, key.Item2), b1 = UvOf(fb.F, key.Item1), b2 = UvOf(fb.F, key.Item2);
            double du1 = a1.U - b1.U, dv1 = a1.V - b1.V, du2 = a2.U - b2.U, dv2 = a2.V - b2.V;
            bool cont = Math.Abs(du1 - du2) < 0.01 && Math.Abs(dv1 - dv2) < 0.01 && Math.Abs(du1 - Math.Round(du1)) < 0.01 && Math.Abs(dv1 - Math.Round(dv1)) < 0.01;
            if (fa.N.Dot(fb.N) > 0.999)
            {
                totCo += len;
                if (cont)
                {
                    contCo += len;
                }
            }
            else
            {
                totCorner += len;
                if (cont)
                {
                    contCorner += len;
                }
            }
        }

        for (int idx = 0; idx < faceList.Count; idx++)
        {
            (Face f, Vec3d[] poly, Vec3d n, double a) = faceList[idx];
            if (!nbTex.TryGetValue(idx, out HashSet<string>? nt) || nt.Count == 0 || nt.Contains(TexName(f)))
            {
                continue;
            }

            Vec3d e1 = Math.Abs(n.Y) < 0.7 ? new Vec3d(-n.Z, 0, n.X).Normalized() : new Vec3d(1, 0, 0);
            Vec3d e2 = n.Cross(e1).Normalized();
            (double l1, double l2) = Extent2(poly, e1, e2);
            if (Math.Min(l1, l2) <= 0.6 && Math.Max(l1, l2) >= 1.5)
            {
                trimN++;
                trimA += a;
            }
        }

        JsonObject? dens = null;
        if (ppm.Count > 0)
        {
            double tw = ppm.Sum(p => p.A);
            List<(double V, double A)> sorted = ppm.OrderBy(p => p.V).ToList();
            double Wp(double q)
            {
                double acc = 0;
                foreach ((double v, double a) in sorted)
                {
                    acc += a;
                    if (acc >= q * tw)
                    {
                        return v;
                    }
                }

                return sorted[^1].V;
            }

            double med = Wp(0.5);
            dens = new JsonObject
            {
                ["coverage"] = R(tw / Math.Max(1e-9, uvKnown)),
                ["ppm_p25"] = R(Wp(0.25)), ["ppm_p50"] = R(med), ["ppm_p75"] = R(Wp(0.75)),
                ["within_25pct"] = R(ppm.Where(p => Math.Abs(Math.Log2(p.V / med)) <= Math.Log2(1.25)).Sum(p => p.A) / tw),
                ["iqr_over_median"] = R((Wp(0.75) - Wp(0.25)) / med),
            };
        }

        double consistent = 0, consTot = 0;
        foreach (IGrouping<string, (string T, double V, double A)> grp in mpt.GroupBy(x => x.T))
        {
            double gm = Med(grp.Select(x => x.V));
            foreach ((string _, double v, double a) in grp)
            {
                consTot += a;
                if (Math.Abs(Math.Log2(v / gm)) <= 0.15)
                {
                    consistent += a;
                }
            }
        }

        static double Overlap(Dictionary<string, double> p, Dictionary<string, double> q)
        {
            double sp = p.Values.Sum(), sq = q.Values.Sum();
            if (sp <= 0 || sq <= 0)
            {
                return double.NaN;
            }

            return p.Keys.Intersect(q.Keys).Sum(k => Math.Min(p[k] / sp, q[k] / sq));
        }

        static int Cover90(Dictionary<string, double> d)
        {
            double s = d.Values.Sum(), acc = 0;
            int c = 0;
            foreach (double v in d.Values.OrderByDescending(x => x))
            {
                acc += v;
                c++;
                if (acc >= 0.9 * s)
                {
                    break;
                }
            }

            return c;
        }

        // Textures seen per cell node: 4 rays at 1 m, plus the floor below and the ceiling above (every other sample).
        List<int> even = _w.PlayIdx.Where(i => (_s[i].I + _s[i].K) % 2 == 0 && cg.NodeOfSample[i] >= 0).ToList();
        var seenTex = new int[even.Count * 6];
        For(even.Count, j =>
        {
            int i = even[j];
            Vec3d o = _s[i].P.Add(new Vec3d(0, 1.0, 0));
            for (int k = 0; k < 16; k += 4)
            {
                seenTex[(j * 6) + (k / 4)] = _sp.Cast(o, SpaceSampler.Directions[k], 12, SpaceSampler.HitFilter.Any, out int fi) is not null && fi >= 0
                    ? geo.Faces[_sp.Faces[fi].Source].Texture : int.MinValue;
            }

            seenTex[(j * 6) + 4] = _sp.Cast(_s[i].P.Add(new Vec3d(0, 0.05, 0)), new Vec3d(0, -1, 0), 1, SpaceSampler.HitFilter.UpFacing, out int ff) is not null && ff >= 0
                ? geo.Faces[_sp.Faces[ff].Source].Texture : int.MinValue;
            seenTex[(j * 6) + 5] = _sp.Cast(_s[i].P.Add(new Vec3d(0, 0.1, 0)), new Vec3d(0, 1, 0), 60, SpaceSampler.HitFilter.DownFacing, out int fc) is not null && fc >= 0
                ? geo.Faces[_sp.Faces[fc].Source].Texture : int.MinValue;
        });
        var nodeTex = new HashSet<int>[cg.N];
        for (int nn = 0; nn < cg.N; nn++)
        {
            nodeTex[nn] = new HashSet<int>();
        }

        for (int j = 0; j < even.Count; j++)
        {
            int nd = cg.NodeOfSample[even[j]];
            for (int k = 0; k < 6; k++)
            {
                if (seenTex[(j * 6) + k] != int.MinValue)
                {
                    nodeTex[nd].Add(seenTex[(j * 6) + k]);
                }
            }
        }

        var families = new Dictionary<string, double>();
        foreach (Dictionary<string, double> role in area)
        {
            foreach (KeyValuePair<string, double> kv in role)
            {
                string fam = kv.Key.Contains('_', StringComparison.Ordinal) ? kv.Key[..kv.Key.IndexOf('_', StringComparison.Ordinal)] : "(none)";
                families[fam] = families.GetValueOrDefault(fam) + kv.Value;
            }
        }

        int allTex = area.SelectMany(d => d.Keys).Distinct().Count();
        _texturingReport = new JsonObject
        {
            ["textures"] = allTex,
            ["textures_per1000m2"] = R(allTex / (_w.PlayArea / 1000)),
            ["families"] = families.Count(f => f.Value >= 0.02 * total),
            ["floor_textures_90"] = Cover90(area[0]), ["wall_textures_90"] = Cover90(area[1]), ["ceiling_textures_90"] = Cover90(area[2]),
            ["floor_wall_overlap"] = R(Overlap(area[0], area[1])),
            ["ceiling_wall_overlap"] = R(Overlap(area[2], area[1])),
            ["textures_per_cell"] = Dist(Enumerable.Range(0, cg.N).Where(n => cg.Area[n] >= 8).Select(n => (double)nodeTex[n].Count)),
            ["uv_offgrid_rotation_fraction"] = R(offRot / Math.Max(1e-9, uvKnown)),
            ["uv_anisotropic_fraction"] = R(aniso / Math.Max(1e-9, uvKnown)),
            ["uv_skewed_fraction"] = R(skew / Math.Max(1e-9, uvKnown)),
            ["uv_edge_aligned_fraction"] = R(edgeAligned / Math.Max(1e-9, uvKnown)),
            ["uv_scale_consistent_per_texture"] = R(consistent / Math.Max(1e-9, consTot)),
            ["uv_continuity_coplanar"] = totCo > 0 ? R(contCo / totCo) : null,
            ["uv_continuity_corner"] = totCorner > 0 ? R(contCorner / totCorner) : null,
            ["trim_faces"] = trimN,
            ["trim_faces_per100"] = R(trimN * 100 / _w.PlayArea),
            ["trim_area_fraction"] = R(trimA / Math.Max(1e-9, total)),
            ["detail_face_area_fraction"] = R(detailA / Math.Max(1e-9, total)),
            ["scroll_area_fraction"] = R(scrollA / Math.Max(1e-9, total)),
            ["metres_per_tile"] = Dist(mpt.Select(x => x.V)),
            ["texel_density"] = dens,
        };
        return _texturingReport;
    }

    private static (double, double) Extent2(Vec3d[] poly, Vec3d e1, Vec3d e2)
    {
        double a0 = double.MaxValue, a1 = double.MinValue, b0 = double.MaxValue, b1 = double.MinValue;
        foreach (Vec3d p in poly)
        {
            double a = p.Dot(e1), b = p.Dot(e2);
            a0 = Math.Min(a0, a);
            a1 = Math.Max(a1, a);
            b0 = Math.Min(b0, b);
            b1 = Math.Max(b1, b);
        }

        return (a1 - a0, b1 - b0);
    }

    /// <summary>The world gradients of u and v across a face (per metre), or null when degenerate.</summary>
    internal static (Vec3d Gu, Vec3d Gv)? UvGrad(Vec3d[] poly, Face f, Vec3d n)
    {
        int m = poly.Length;
        if (f.Vertices.Count != m)
        {
            return null;
        }

        int bi = -1, bj = -1;
        double best = 0;
        for (int i = 1; i < m; i++)
        {
            for (int j = i + 1; j < m; j++)
            {
                double ar = poly[i].Sub(poly[0]).Cross(poly[j].Sub(poly[0])).Length;
                if (ar > best)
                {
                    best = ar;
                    bi = i;
                    bj = j;
                }
            }
        }

        if (best < 1e-4)
        {
            return null;
        }

        Vec3d e1 = poly[bi].Sub(poly[0]).Normalized();
        Vec3d e2 = n.Cross(e1).Normalized();
        double s1 = poly[bi].Sub(poly[0]).Dot(e1), t1 = poly[bi].Sub(poly[0]).Dot(e2);
        double s2 = poly[bj].Sub(poly[0]).Dot(e1), t2 = poly[bj].Sub(poly[0]).Dot(e2);
        double det = (s1 * t2) - (s2 * t1);
        if (Math.Abs(det) < 1e-6)
        {
            return null;
        }

        Uv uv0 = f.Vertices[0].TextureCoords, uv1 = f.Vertices[bi].TextureCoords, uv2 = f.Vertices[bj].TextureCoords;
        double du1 = uv1.U - uv0.U, du2 = uv2.U - uv0.U, dv1 = uv1.V - uv0.V, dv2 = uv2.V - uv0.V;
        double au = ((du1 * t2) - (du2 * t1)) / det, bu = ((s1 * du2) - (s2 * du1)) / det;
        double av = ((dv1 * t2) - (dv2 * t1)) / det, bv = ((s1 * dv2) - (s2 * dv1)) / det;
        return (e1.Scale(au).Add(e2.Scale(bu)), e1.Scale(av).Add(e2.Scale(bv)));
    }
}

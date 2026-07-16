using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Flagship 24 diagnostics: portal-side face-vote vs the geometric probe. Pure diagnostics; no asserts.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class Pass24Diag
{
    private readonly ITestOutputHelper _out;

    public Pass24Diag(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    public void Compare_Portal_Modes(string file)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? red = null;
        List<RoomEffect> effects = new();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                red ??= gs.Geometry;
            }
            else if (s.Content is RoomEffectsSection es)
            {
                effects = es.Effects;
            }
        }

        if (red is null)
        {
            return;
        }

        List<Brush> brushes = MoverBrushes.StaticWorldBrushes(rfl);

        Geometry probe = CompileMode(brushes, effects, rfl.Context.IsAlpine, faceVote: false, alwaysVote: false);
        Geometry hybrid = CompileMode(brushes, effects, rfl.Context.IsAlpine, faceVote: true, alwaysVote: false);
        Geometry whole = CompileMode(brushes, effects, rfl.Context.IsAlpine, faceVote: true, alwaysVote: true);

        var sb = new StringBuilder();
        sb.AppendLine($"PORTAL MODE COMPARE — {file}");
        sb.AppendLine($"RED portals={red.Portals.Count}  probe={probe.Portals.Count}  hybrid={hybrid.Portals.Count}  wholesale={whole.Portals.Count}");
        sb.AppendLine();
        sb.AppendLine($"liquid window area (m^2): RED={LiquidWindow(red):F1}  probe={LiquidWindow(probe):F1}  hybrid={LiquidWindow(hybrid):F1}  wholesale={LiquidWindow(whole):F1}");
        sb.AppendLine();

        DumpPairs(sb, "PROBE", probe);
        DumpPairs(sb, "HYBRID", hybrid);
        DumpPairs(sb, "WHOLESALE", whole);

        // Which room-pairs does probe have that wholesale lost (candidate-selection regressions)?
        var probePairs = PairSet(probe);
        var wholePairs = PairSet(whole);
        var hybridPairs = PairSet(hybrid);
        sb.AppendLine();
        sb.AppendLine("== pairs in PROBE but NOT in WHOLESALE (lost by pure vote) ==");
        foreach ((int, int) k in probePairs.Keys.Where(k => !wholePairs.ContainsKey(k)).OrderBy(k => k))
        {
            sb.AppendLine($"  {Describe(probe, probePairs[k])}");
        }

        sb.AppendLine("== pairs in WHOLESALE but NOT in PROBE (new by pure vote) ==");
        foreach ((int, int) k in wholePairs.Keys.Where(k => !probePairs.ContainsKey(k)).OrderBy(k => k))
        {
            sb.AppendLine($"  {Describe(whole, wholePairs[k])}");
        }

        sb.AppendLine("== pairs in PROBE but NOT in HYBRID ==");
        foreach ((int, int) k in probePairs.Keys.Where(k => !hybridPairs.ContainsKey(k)).OrderBy(k => k))
        {
            sb.AppendLine($"  {Describe(probe, probePairs[k])}");
        }

        sb.AppendLine("== pairs in HYBRID but NOT in PROBE ==");
        foreach ((int, int) k in hybridPairs.Keys.Where(k => !probePairs.ContainsKey(k)).OrderBy(k => k))
        {
            sb.AppendLine($"  {Describe(hybrid, hybridPairs[k])}");
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact($"pass24_portalmodes_{Path.GetFileNameWithoutExtension(file)}.txt", sb.ToString());
    }

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("ctf06.rfl")]
    [InlineData("dm03.rfl")]
    [InlineData("kothcowb1~.rfl")]
    public void Dump_Liquid_Coverage(string file)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? red = null;
        List<RoomEffect> effects = new();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                red ??= gs.Geometry;
            }
            else if (s.Content is RoomEffectsSection es)
            {
                effects = es.Effects;
            }
        }

        if (red is null)
        {
            return;
        }

        List<Brush> brushes = MoverBrushes.StaticWorldBrushes(rfl);
        Geometry ged = CompileMode(brushes, effects, rfl.Context.IsAlpine, faceVote: true, alwaysVote: false);

        var sb = new StringBuilder();
        sb.AppendLine($"LIQUID SURFACE XZ COVERAGE — {file}");

        var redUp = UpLiquidFaces(red);
        var gedUp = UpLiquidFaces(ged);
        sb.AppendLine($"up-facing liquid faces: RED={redUp.Count} GED={gedUp.Count}");
        sb.AppendLine($"up-facing area: RED={redUp.Sum(f => FaceArea(red, f)):F1} GED={gedUp.Sum(f => FaceArea(ged, f)):F1}");
        sb.AppendLine();

        // Bounding XZ over both surfaces.
        float xmin = float.MaxValue, xmax = float.MinValue, zmin = float.MaxValue, zmax = float.MinValue;
        foreach ((Geometry g, List<Face> up) in new[] { (red, redUp), (ged, gedUp) })
        {
            foreach (Face f in up)
            {
                foreach (FaceVertex v in f.Vertices)
                {
                    Vec3 p = g.Vertices[v.Index];
                    xmin = Math.Min(xmin, p.X);
                    xmax = Math.Max(xmax, p.X);
                    zmin = Math.Min(zmin, p.Z);
                    zmax = Math.Max(zmax, p.Z);
                }
            }
        }

        const float Cell = 0.25f;
        int nx = (int)((xmax - xmin) / Cell) + 1;
        int nz = (int)((zmax - zmin) / Cell) + 1;
        int redOnly = 0, gedOnly = 0, both = 0, gedOverlap = 0;
        var holeGrid = new bool[nx, nz];
        for (int ix = 0; ix < nx; ix++)
        {
            for (int iz = 0; iz < nz; iz++)
            {
                float px = xmin + ((ix + 0.5f) * Cell);
                float pz = zmin + ((iz + 0.5f) * Cell);
                int rc = CoverCount(red, redUp, px, pz);
                int gc = CoverCount(ged, gedUp, px, pz);
                bool r = rc > 0, gd = gc > 0;
                if (r && gd)
                {
                    both++;
                }
                else if (r)
                {
                    redOnly++; // RED covers, GED does NOT — a HOLE in GED's surface
                    holeGrid[ix, iz] = true;
                }
                else if (gd)
                {
                    gedOnly++; // GED covers, RED does NOT — surface poking past RED's extent
                }

                if (gc >= 2)
                {
                    gedOverlap++; // GED overlaps itself here (multiple up-faces) — compensating overlap
                }
            }
        }

        // Largest CONTIGUOUS hole (4-connected flood) — a single big gap is the visible defect; scattered
        // 1-cell edge slivers between two differently-tessellated pool rims are not.
        int largestHole = LargestComponent(holeGrid, nx, nz);

        float cellA = Cell * Cell;
        sb.AppendLine($"grid {nx}x{nz} @ {Cell}m over X[{xmin:F1}..{xmax:F1}] Z[{zmin:F1}..{zmax:F1}]");
        sb.AppendLine($"both-covered cells: {both} ({both * cellA:F1} m²)");
        sb.AppendLine($"RED-only (GED HOLE): {redOnly} ({redOnly * cellA:F1} m²)");
        sb.AppendLine($"GED-only (overshoot): {gedOnly} ({gedOnly * cellA:F1} m²)");
        sb.AppendLine($"GED self-overlap cells (>=2 up-faces): {gedOverlap} ({gedOverlap * cellA:F1} m²)");
        sb.AppendLine($"LARGEST CONTIGUOUS GED hole: {largestHole} cells ({largestHole * cellA:F1} m²)");

        // Shoelace (true signed) area vs fan (abs) area — a gap means non-convex / self-intersecting faces.
        float gedShoe = gedUp.Sum(f => ShoelaceXz(ged, f));
        float redShoe = redUp.Sum(f => ShoelaceXz(red, f));
        int gedBad = gedUp.Count(f => !IsSimpleConvexXz(ged, f));
        int redBad = redUp.Count(f => !IsSimpleConvexXz(red, f));
        sb.AppendLine();
        sb.AppendLine($"shoelace-XZ area (true): RED={redShoe:F1} GED={gedShoe:F1}   (fan-area RED={redUp.Sum(f => FaceArea(red, f)):F1} GED={gedUp.Sum(f => FaceArea(ged, f)):F1})");
        sb.AppendLine($"non-convex-or-self-intersecting up-faces: RED={redBad} GED={gedBad}");

        // Dump GED up-faces sorted by area (the merge output shape); flag bad ones.
        sb.AppendLine();
        sb.AppendLine("== GED up-faces (fanArea, shoelaceXZ, verts, convex?, XZ bbox) ==");
        foreach (Face f in gedUp.OrderByDescending(f => FaceArea(ged, f)).Take(33))
        {
            (float fx0, float fx1, float fz0, float fz1) = XzBox(ged, f);
            bool ok = IsSimpleConvexXz(ged, f);
            sb.AppendLine($"  fan={FaceArea(ged, f),7:F1} shoe={ShoelaceXz(ged, f),7:F1} verts={f.Vertices.Count,3} {(ok ? "OK  " : "BAD ")} X[{fx0,6:F1}..{fx1,6:F1}] Z[{fz0,6:F1}..{fz1,6:F1}]");
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact($"pass24_liqcover_{Path.GetFileNameWithoutExtension(file)}.txt", sb.ToString());
    }

    private static int LargestComponent(bool[,] grid, int nx, int nz)
    {
        var seen = new bool[nx, nz];
        int best = 0;
        var stack = new Stack<(int, int)>();
        for (int ix = 0; ix < nx; ix++)
        {
            for (int iz = 0; iz < nz; iz++)
            {
                if (!grid[ix, iz] || seen[ix, iz])
                {
                    continue;
                }

                int size = 0;
                stack.Push((ix, iz));
                seen[ix, iz] = true;
                while (stack.Count > 0)
                {
                    (int cx, int cz) = stack.Pop();
                    size++;
                    foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nxp = cx + dx, nzp = cz + dz;
                        if (nxp >= 0 && nxp < nx && nzp >= 0 && nzp < nz && grid[nxp, nzp] && !seen[nxp, nzp])
                        {
                            seen[nxp, nzp] = true;
                            stack.Push((nxp, nzp));
                        }
                    }
                }

                best = Math.Max(best, size);
            }
        }

        return best;
    }

    private static List<Face> UpLiquidFaces(Geometry g)
    {
        var list = new List<Face>();
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            bool flagged = ((FaceFlags)f.Flags & FaceFlags.LiquidSurface) != 0;
            bool wtr = f.Texture >= 0 && f.Texture < g.Textures.Count &&
                       g.Textures[f.Texture].StartsWith("wtr_", StringComparison.OrdinalIgnoreCase);
            if ((flagged || wtr) && FaceNormalY(g, f) > 0.5f)
            {
                list.Add(f);
            }
        }

        return list;
    }

    private static float FaceNormalY(Geometry g, Face f)
    {
        // Newell Y component.
        float ny = 0;
        for (int i = 0; i < f.Vertices.Count; i++)
        {
            Vec3 a = g.Vertices[f.Vertices[i].Index];
            Vec3 b = g.Vertices[f.Vertices[(i + 1) % f.Vertices.Count].Index];
            ny += (a.Z - b.Z) * (a.X + b.X);
        }

        return ny;
    }

    private static int CoverCount(Geometry g, List<Face> faces, float px, float pz)
    {
        int c = 0;
        foreach (Face f in faces)
        {
            if (PointInFaceXz(g, f, px, pz))
            {
                c++;
            }
        }

        return c;
    }

    private static bool PointInFaceXz(Geometry g, Face f, float px, float pz)
    {
        bool inside = false;
        int n = f.Vertices.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vec3 vi = g.Vertices[f.Vertices[i].Index];
            Vec3 vj = g.Vertices[f.Vertices[j].Index];
            if (((vi.Z > pz) != (vj.Z > pz)) && (px < ((vj.X - vi.X) * (pz - vi.Z) / (vj.Z - vi.Z)) + vi.X))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Signed shoelace area in XZ (absolute) — the TRUE covered area of a simple polygon; a
    /// self-intersecting polygon's shoelace is LESS than its fan (abs-triangle) area.</summary>
    private static float ShoelaceXz(Geometry g, Face f)
    {
        float a = 0;
        int n = f.Vertices.Count;
        for (int i = 0; i < n; i++)
        {
            Vec3 p = g.Vertices[f.Vertices[i].Index];
            Vec3 q = g.Vertices[f.Vertices[(i + 1) % n].Index];
            a += (p.X * q.Z) - (q.X * p.Z);
        }

        return Math.Abs(a) * 0.5f;
    }

    /// <summary>True when the polygon projected to XZ is simple and convex (all turns one way, no
    /// self-crossing). RED's convex-decomposition faces pass; an over-merged non-convex face fails.</summary>
    private static bool IsSimpleConvexXz(Geometry g, Face f)
    {
        int n = f.Vertices.Count;
        if (n < 3)
        {
            return false;
        }

        int sign = 0;
        for (int i = 0; i < n; i++)
        {
            Vec3 a = g.Vertices[f.Vertices[i].Index];
            Vec3 b = g.Vertices[f.Vertices[(i + 1) % n].Index];
            Vec3 c = g.Vertices[f.Vertices[(i + 2) % n].Index];
            float cross = ((b.X - a.X) * (c.Z - b.Z)) - ((b.Z - a.Z) * (c.X - b.X));
            if (Math.Abs(cross) < 1e-5f)
            {
                continue;
            }

            int s = cross > 0 ? 1 : -1;
            if (sign == 0)
            {
                sign = s;
            }
            else if (s != sign)
            {
                return false;
            }
        }

        return true;
    }

    private static (float, float, float, float) XzBox(Geometry g, Face f)
    {
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        foreach (FaceVertex v in f.Vertices)
        {
            Vec3 p = g.Vertices[v.Index];
            x0 = Math.Min(x0, p.X);
            x1 = Math.Max(x1, p.X);
            z0 = Math.Min(z0, p.Z);
            z1 = Math.Max(z1, p.Z);
        }

        return (x0, x1, z0, z1);
    }

    private static float FaceArea(Geometry g, Face f)
    {
        var c = new Vec3(0, 0, 0);
        foreach (FaceVertex v in f.Vertices)
        {
            c = c.Add(g.Vertices[v.Index]);
        }

        c = c.Scale(1f / f.Vertices.Count);
        float area = 0;
        for (int i = 0; i < f.Vertices.Count; i++)
        {
            Vec3 a = g.Vertices[f.Vertices[i].Index].Sub(c);
            Vec3 b = g.Vertices[f.Vertices[(i + 1) % f.Vertices.Count].Index].Sub(c);
            area += a.Cross(b).Length() * 0.5f;
        }

        return area;
    }

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("dmedificationduelb2.rfl")]
    [InlineData("kothcowb1~.rfl")]
    public void Dump_Membrane_Detail(string file)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        List<RoomEffect> effects = new();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is RoomEffectsSection es)
            {
                effects = es.Effects;
            }
        }

        List<Brush> brushes = MoverBrushes.StaticWorldBrushes(rfl);

        var sb = new StringBuilder();
        RoomBuilder.CaptureJoins = true;
        try
        {
            RoomBuilder.ForceAlwaysVote = false;
            CompiledLevel c = GeometryCompiler.Compile(brushes, effects,
                new CompileOptions { Alpine = rfl.Context.IsAlpine, BuildSurfaces = false, PortalFaceVote = true });
            var det = RoomBuilder.CapturedMembraneDetail;
            sb.AppendLine($"MEMBRANE DETAIL — {file}  (GED portals={c.Geometry.Portals.Count})");
            sb.AppendLine($"{"uid",6} {"normal",-20} {"off",8} {"frags",5} {"area",8} {"drop%",6} {"vote",4} {"grp",4} {"front",5} {"back",5}  footprintY");
            if (det is not null)
            {
                foreach (var d in det.OrderBy(x => x.Group).ThenBy(x => x.Uid))
                {
                    float dropPct = d.Area > 0 ? d.DropArea / d.Area : 0;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0,6} ({1,5:F2},{2,5:F2},{3,5:F2}) {4,8:F2} {5,5} {6,8:F1} {7,5:P0} {8,4} {9,4} {10,5} {11,5}  y[{12:F2}..{13:F2}]",
                        d.Uid, d.Normal.X, d.Normal.Y, d.Normal.Z, d.Offset, d.Frags, d.Area, dropPct,
                        d.Voted ? "V" : ".", d.Group, d.Front, d.Back, d.FpMin.Y, d.FpMax.Y));
                }
            }

            _out.WriteLine(sb.ToString());
            WriteArtifact($"pass24_membranes_{Path.GetFileNameWithoutExtension(file)}.txt", sb.ToString());
        }
        finally
        {
            RoomBuilder.CaptureJoins = false;
        }
    }

    [Fact]
    public void Sweep_Portal_Parity_Corpus()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("PORTAL/ROOM PARITY CORPUS SWEEP — RED vs GED(probe) vs GED(vote)");
        sb.AppendLine($"{"level",-28} {"REDp",5} {"prbP",5} {"votP",5} {"REDm",5} {"votM",5} {"lnkRep",7} {"lnkMis",7} {"lnkXtr",7} {"liqRED",7} {"liqVOT",7} {"areaPar",8}");

        double areaNum = 0, areaDen = 0;
        int levels = 0, regressed = 0;
        var regressions = new List<string>();
        foreach (string path in Corpus.RflFiles)
        {
            string name = Path.GetFileName(path);
            if (name.Contains(".autosave", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ged", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            RflFile rfl;
            Geometry? red = null;
            List<RoomEffect> effects = new();
            try
            {
                rfl = RflFile.Load(path);
                rfl.ParseAllKnownSections();
                foreach (RflSection s in rfl.Sections)
                {
                    if (s.Content is GeometrySection gs)
                    {
                        red ??= gs.Geometry;
                    }
                    else if (s.Content is RoomEffectsSection es)
                    {
                        effects = es.Effects;
                    }
                }
            }
            catch
            {
                continue;
            }

            if (red is null)
            {
                continue;
            }

            List<Brush> brushes = MoverBrushes.StaticWorldBrushes(rfl);
            Geometry probe, vote;
            try
            {
                probe = CompileMode(brushes, effects, rfl.Context.IsAlpine, faceVote: false, alwaysVote: false);
                vote = CompileMode(brushes, effects, rfl.Context.IsAlpine, faceVote: true, alwaysVote: false);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-28} EXCEPTION {ex.GetType().Name}");
                continue;
            }

            levels++;
            (int rep, int mis, int xtr) = LinkDiff(red, vote);
            (int repP, int misP, int _) = LinkDiff(red, probe);
            float liqRed = LiquidWindow(red), liqVote = LiquidWindow(vote);
            (double num, double den) = AreaParity(red, vote);
            areaNum += num;
            areaDen += den;

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-28} {1,5} {2,5} {3,5} {4,5} {5,5} {6,7} {7,7} {8,7} {9,7:F0} {10,7:F0} {11,8:P0}",
                name.Length > 28 ? name[..28] : name,
                red.Portals.Count, probe.Portals.Count, vote.Portals.Count,
                MainCount(red), MainCount(vote), rep, mis, xtr, liqRed, liqVote, den <= 0 ? 1.0 : num / den));

            // Regression = the vote reproduces FEWER RED links than the probe did, or introduces MORE extras.
            if (mis > misP)
            {
                regressed++;
                regressions.Add($"{name}: link-missing probe={misP} -> vote={mis}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"levels={levels}  link-regressions(vote worse than probe)={regressed}");
        sb.AppendLine($"aggregate per-portal window-area parity (RED-link portals): {(areaDen <= 0 ? 1.0 : areaNum / areaDen):P1}");
        foreach (string r in regressions)
        {
            sb.AppendLine($"  REGRESSION {r}");
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact("pass24_portal_corpus_sweep.txt", sb.ToString());
    }

    private static int MainCount(Geometry g) => g.Rooms.Count(r => r.IsSubroom == 0);

    /// <summary>Greedy IoU main-room correspondence, then diff RED's portal link set vs GED's through it.</summary>
    private static (int Reproduced, int Missing, int Extra) LinkDiff(Geometry red, Geometry ged)
    {
        var redMain = red.Rooms.Where(r => r.IsSubroom == 0).ToList();
        var gedMain = ged.Rooms.Where(r => r.IsSubroom == 0).ToList();
        int[] redToGed = GreedyMatch(redMain, gedMain);

        Dictionary<int, int> redSlot = SlotOfRoom(red), gedSlot = SlotOfRoom(ged);
        HashSet<(int, int)> redLinks = LinkSet(red, redSlot), gedLinks = LinkSet(ged, gedSlot);

        var redInGed = new HashSet<(int, int)>();
        foreach ((int a, int b) in redLinks)
        {
            int ga = redToGed[a], gb = redToGed[b];
            if (ga < 0 || gb < 0)
            {
                continue;
            }

            redInGed.Add(ga < gb ? (ga, gb) : (gb, ga));
        }

        int rep = redInGed.Count(l => gedLinks.Contains(l));
        return (rep, redInGed.Count - rep, gedLinks.Count - rep);
    }

    /// <summary>Aggregate per-portal window-area parity: for each reproduced RED link, min(gedArea,redArea)/max.</summary>
    private static (double Num, double Den) AreaParity(Geometry red, Geometry ged)
    {
        var redMain = red.Rooms.Where(r => r.IsSubroom == 0).ToList();
        var gedMain = ged.Rooms.Where(r => r.IsSubroom == 0).ToList();
        int[] redToGed = GreedyMatch(redMain, gedMain);
        Dictionary<int, int> redSlot = SlotOfRoom(red), gedSlot = SlotOfRoom(ged);

        // Map GED link pair -> window area.
        var gedArea = new Dictionary<(int, int), float>();
        foreach (Portal p in ged.Portals)
        {
            if (!gedSlot.TryGetValue(p.RoomIndex1, out int a) || !gedSlot.TryGetValue(p.RoomIndex2, out int b) || a == b)
            {
                continue;
            }

            var k = a < b ? (a, b) : (b, a);
            gedArea[k] = Math.Max(gedArea.GetValueOrDefault(k), WindowArea(p));
        }

        double num = 0, den = 0;
        foreach (Portal p in red.Portals)
        {
            if (!redSlot.TryGetValue(p.RoomIndex1, out int ra) || !redSlot.TryGetValue(p.RoomIndex2, out int rb) || ra == rb)
            {
                continue;
            }

            int ga = redToGed[ra], gb = redToGed[rb];
            if (ga < 0 || gb < 0)
            {
                continue;
            }

            var k = ga < gb ? (ga, gb) : (gb, ga);
            if (!gedArea.TryGetValue(k, out float garea))
            {
                den += WindowArea(p); // missing link: 0 parity contribution
                continue;
            }

            float rarea = WindowArea(p);
            num += Math.Min(garea, rarea);
            den += Math.Max(garea, rarea);
        }

        return (num, den);
    }

    private static int[] GreedyMatch(List<Room> redMain, List<Room> gedMain)
    {
        int nr = redMain.Count, ng = gedMain.Count;
        var redToGed = new int[nr];
        var gedToRed = new int[ng];
        Array.Fill(redToGed, -1);
        Array.Fill(gedToRed, -1);
        var cands = new List<(double Iou, int R, int G)>();
        for (int r = 0; r < nr; r++)
        {
            for (int gi = 0; gi < ng; gi++)
            {
                double iou = Iou(redMain[r].Aabb, gedMain[gi].Aabb);
                if (iou >= 0.10)
                {
                    cands.Add((iou, r, gi));
                }
            }
        }

        cands.Sort((a, b) => b.Iou.CompareTo(a.Iou));
        foreach ((double _, int r, int gi) in cands)
        {
            if (redToGed[r] < 0 && gedToRed[gi] < 0)
            {
                redToGed[r] = gi;
                gedToRed[gi] = r;
            }
        }

        return redToGed;
    }

    private static Dictionary<int, int> SlotOfRoom(Geometry g)
    {
        var d = new Dictionary<int, int>();
        int slot = 0;
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            if (g.Rooms[i].IsSubroom == 0)
            {
                d[i] = slot++;
            }
        }

        return d;
    }

    private static HashSet<(int, int)> LinkSet(Geometry g, Dictionary<int, int> mainSlot)
    {
        var set = new HashSet<(int, int)>();
        foreach (Portal p in g.Portals)
        {
            if (!mainSlot.TryGetValue(p.RoomIndex1, out int a) || !mainSlot.TryGetValue(p.RoomIndex2, out int b) || a == b)
            {
                continue;
            }

            set.Add(a < b ? (a, b) : (b, a));
        }

        return set;
    }

    private static double Iou(Aabb a, Aabb b)
    {
        float ix = Math.Max(0, Math.Min(a.P2.X, b.P2.X) - Math.Max(a.P1.X, b.P1.X));
        float iy = Math.Max(0, Math.Min(a.P2.Y, b.P2.Y) - Math.Max(a.P1.Y, b.P1.Y));
        float iz = Math.Max(0, Math.Min(a.P2.Z, b.P2.Z) - Math.Max(a.P1.Z, b.P1.Z));
        double inter = (double)ix * iy * iz;
        double vol = (Math.Abs((double)(a.P2.X - a.P1.X) * (a.P2.Y - a.P1.Y) * (a.P2.Z - a.P1.Z)))
                   + (Math.Abs((double)(b.P2.X - b.P1.X) * (b.P2.Y - b.P1.Y) * (b.P2.Z - b.P1.Z))) - inter;
        return vol <= 0 ? 0 : inter / vol;
    }

    private static Geometry CompileMode(List<Brush> brushes, List<RoomEffect> effects, bool alpine, bool faceVote, bool alwaysVote)
    {
        RoomBuilder.ForceAlwaysVote = alwaysVote;
        try
        {
            return GeometryCompiler.Compile(brushes, effects,
                new CompileOptions { Alpine = alpine, BuildSurfaces = false, PortalFaceVote = faceVote }).Geometry;
        }
        finally
        {
            RoomBuilder.ForceAlwaysVote = false;
        }
    }

    private static float LiquidWindow(Geometry g)
    {
        int liq = -1;
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            if (g.Rooms[i].IsLiquidRoom != 0)
            {
                liq = i;
                break;
            }
        }

        if (liq < 0)
        {
            return 0;
        }

        float best = 0;
        foreach (Portal p in g.Portals)
        {
            if (p.RoomIndex1 != liq && p.RoomIndex2 != liq)
            {
                continue;
            }

            best = Math.Max(best, WindowArea(p));
        }

        return best;
    }

    private static float WindowArea(Portal p)
    {
        float dx = Math.Abs(p.Point2.X - p.Point1.X);
        float dy = Math.Abs(p.Point2.Y - p.Point1.Y);
        float dz = Math.Abs(p.Point2.Z - p.Point1.Z);
        // Largest two dims (a portal window is a rectangle; the thin dim is ~0).
        float min = Math.Min(dx, Math.Min(dy, dz));
        float prod = dx * dy * dz;
        return min > 1e-4f ? prod / min : Math.Max(dx * dy, Math.Max(dy * dz, dx * dz));
    }

    private static Dictionary<(int, int), int> PairSet(Geometry g)
    {
        var d = new Dictionary<(int, int), int>();
        for (int i = 0; i < g.Portals.Count; i++)
        {
            Portal p = g.Portals[i];
            var k = p.RoomIndex1 < p.RoomIndex2 ? (p.RoomIndex1, p.RoomIndex2) : (p.RoomIndex2, p.RoomIndex1);
            d[k] = i;
        }

        return d;
    }

    private static void DumpPairs(StringBuilder sb, string tag, Geometry g)
    {
        sb.AppendLine($"== {tag} portals ({g.Portals.Count}) ==");
        for (int i = 0; i < g.Portals.Count; i++)
        {
            sb.AppendLine($"  {Describe(g, i)}");
        }

        sb.AppendLine();
    }

    private static string Describe(Geometry g, int i)
    {
        Portal p = g.Portals[i];
        float dx = Math.Abs(p.Point2.X - p.Point1.X);
        float dy = Math.Abs(p.Point2.Y - p.Point1.Y);
        float dz = Math.Abs(p.Point2.Z - p.Point1.Z);
        float cx = (p.Point1.X + p.Point2.X) / 2, cy = (p.Point1.Y + p.Point2.Y) / 2, cz = (p.Point1.Z + p.Point2.Z) / 2;
        int liq1 = g.Rooms.Count > p.RoomIndex1 && p.RoomIndex1 >= 0 ? g.Rooms[p.RoomIndex1].IsLiquidRoom : 0;
        int liq2 = g.Rooms.Count > p.RoomIndex2 && p.RoomIndex2 >= 0 ? g.Rooms[p.RoomIndex2].IsLiquidRoom : 0;
        string liqTag = (liq1 != 0 || liq2 != 0) ? " LIQ" : "";
        return string.Format(CultureInfo.InvariantCulture,
            "p#{0,-3} {1,3}<->{2,-3} c=({3,7:F1},{4,7:F1},{5,7:F1}) size=({6,6:F2}x{7,6:F2}x{8,6:F2}) area={9,7:F1}{10}",
            i, p.RoomIndex1, p.RoomIndex2, cx, cy, cz, dx, dy, dz, WindowArea(p), liqTag);
    }

    private static void WriteArtifact(string name, string text)
    {
        DirectoryInfo? dir = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Directory.GetCurrentDirectory());
        if (dir is null)
        {
            return;
        }

        string outDir = Path.Combine(dir.FullName, "tests", "artifacts");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, name), text);
    }

    private static DirectoryInfo? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        return dir;
    }
}

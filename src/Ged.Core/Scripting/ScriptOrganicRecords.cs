using System.Linq;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>The result of <c>geo.tube</c> / <c>geo.passage</c> (Lua type <c>tube</c>).</summary>
[ScriptDoc("A tube from geo.tube or geo.passage: its air brushes in station order, the stations, the climbing legs, bounds and length.", Since = 2)]
public sealed class ScriptTube
{
    private readonly ScriptTubeStation[] _stations;
    private readonly ScriptTubeClimb[] _climbs;

    internal ScriptTube(ScriptBrushQuery brushes, TubeResult result, long seed)
    {
        Brushes = brushes;
        Result = result;
        Seed = seed;
        _stations = result.Stations.Select(s => new ScriptTubeStation(s)).ToArray();
        _climbs = result.Climbs.Select(c => new ScriptTubeClimb(c)).ToArray();
    }

    /// <summary>The generator output behind the record (stations, floors) — read by <c>geo.scatter{along = …}</c>.</summary>
    internal TubeResult Result { get; }

    [ScriptDoc("The air brushes (brush_query, station order).", Since = 2)]
    public ScriptBrushQuery Brushes { get; }

    [ScriptDoc("The stations in path order (tube_station[]): floor point, floor height, width, clear height and heading.", Since = 2)]
    public ScriptTubeStation[] Stations => _stations.ToArray();

    [ScriptDoc("The climbing and falling legs (tube_climb[]), path order.", Since = 2)]
    public ScriptTubeClimb[] Climbs => _climbs.ToArray();

    [ScriptDoc("The bounds minimum of every brush (vec).", Since = 2)]
    public Vec3 Min => ScriptOrganic.ToVec3(Result.Min);

    [ScriptDoc("The bounds maximum of every brush (vec).", Since = 2)]
    public Vec3 Max => ScriptOrganic.ToVec3(Result.Max);

    [ScriptDoc("The walk floor height at the first station, metres.", Since = 2)]
    public double FloorY => Result.Stations[0].FloorY;

    [ScriptDoc("The canyon's common rim height (one horizontal plane over every segment), metres; nil for a cave or passage.", Since = 2)]
    public double? TopY => Result.TopY;

    [ScriptDoc("The first station's floor point (vec).", Since = 2)]
    public Vec3 Start => ScriptOrganic.ToVec3(Result.Stations[0].Pos);

    [ScriptDoc("The last station's floor point (vec); read it as t[\"end\"].", Since = 2)]
    public Vec3 End => ScriptOrganic.ToVec3(Result.Stations[^1].Pos);

    [ScriptDoc("The horizontal path length, metres.", Since = 2)]
    public double Length => Result.Length;

    [ScriptDoc("How many ring re-draws (and roughness fallbacks) placing the segments took; 0 for a passage.", Since = 2)]
    public int Retries => Result.Retries;

    [ScriptDoc("The walkway width guaranteed along the path, metres (0 when unchecked).", Since = 2)]
    public double ClearWidth => Result.ClearWidth;

    [ScriptDoc("The seed the tube was built with.", Since = 2)]
    public double Seed { get; }

    public override string ToString() => $"tube({Brushes.Count} brushes, {Result.Length:0.#} m)";
}

/// <summary>One station of a tube (Lua type <c>tube_station</c>).</summary>
[ScriptDoc("One tube station: the floor point on the path, its walk floor height, nominal floor width, clear height and heading.", Since = 2)]
public sealed class ScriptTubeStation
{
    private readonly TubeStation _s;

    internal ScriptTubeStation(TubeStation s) => _s = s;

    [ScriptDoc("The floor point on the path (vec; y is the walk floor).", Since = 2)]
    public Vec3 Pos => ScriptOrganic.ToVec3(_s.Pos);

    [ScriptDoc("The walk floor height here, metres (a multiple of 1/16).", Since = 2)]
    public double FloorY => _s.FloorY;

    [ScriptDoc("The nominal floor width here, metres.", Since = 2)]
    public double Width => _s.Width;

    [ScriptDoc("The clear height here, metres (cave: at the crown; canyon: to the rim).", Since = 2)]
    public double Height => _s.Height;

    [ScriptDoc("The path heading here, yaw degrees (0 faces +Z, positive turns toward +X).", Since = 2)]
    public double Heading => _s.Heading;

    public override string ToString() => $"tube_station({_s.Pos})";
}

/// <summary>One climbing or falling leg of a tube (Lua type <c>tube_climb</c>).</summary>
[ScriptDoc("One climbing or falling leg of a tube (a smooth slope of at most max_slope): where it starts and ends in path order, the signed rise, run, heading and walkway width.", Since = 2)]
public sealed class ScriptTubeClimb
{
    private readonly TubeClimb _c;

    internal ScriptTubeClimb(TubeClimb c) => _c = c;

    [ScriptDoc("Where the leg starts, in path order (vec on the floor).", Since = 2)]
    public Vec3 From => ScriptOrganic.ToVec3(_c.From);

    [ScriptDoc("Where the leg ends, in path order (vec on the floor).", Since = 2)]
    public Vec3 To => ScriptOrganic.ToVec3(_c.To);

    [ScriptDoc("The height gained along the path, metres (negative when the leg falls).", Since = 2)]
    public double Rise => _c.Rise;

    [ScriptDoc("The horizontal run, metres.", Since = 2)]
    public double Run => _c.Run;

    [ScriptDoc("The heading of the leg, yaw degrees.", Since = 2)]
    public double Heading => _c.Heading;

    [ScriptDoc("The walkway width guaranteed along the leg, metres.", Since = 2)]
    public double Width => _c.Width;

    public override string ToString() => $"tube_climb({_c.Rise:0.##} m over {_c.Run:0.##} m)";
}

/// <summary>The result of <c>geo.rock_row</c> / <c>geo.scatter</c> (Lua type <c>rocks</c>).</summary>
[ScriptDoc("Rocks from geo.rock_row or geo.scatter: the brushes, their count, bounds and seed.", Since = 2)]
public sealed class ScriptRocks
{
    private readonly RockResult _r;

    internal ScriptRocks(ScriptBrushQuery brushes, RockResult r, long seed)
    {
        Brushes = brushes;
        _r = r;
        Seed = seed;
    }

    [ScriptDoc("The rock brushes (brush_query, emission order).", Since = 2)]
    public ScriptBrushQuery Brushes { get; }

    [ScriptDoc("The number of rocks.", Since = 2)]
    public int Count => Brushes.Count;

    [ScriptDoc("The bounds minimum of every rock (vec).", Since = 2)]
    public Vec3 Min => ScriptOrganic.ToVec3(_r.Min);

    [ScriptDoc("The bounds maximum of every rock (vec).", Since = 2)]
    public Vec3 Max => ScriptOrganic.ToVec3(_r.Max);

    [ScriptDoc("The seed the rocks were built with.", Since = 2)]
    public double Seed { get; }

    public override string ToString() => $"rocks({Count})";
}

/// <summary>The result of <c>geo.pit</c> (Lua type <c>pit</c>).</summary>
[ScriptDoc("A pit from geo.pit: its air brushes top to bottom, the flat floor height, bounds, centre, radius, depth and seed.", Since = 2)]
public sealed class ScriptPit
{
    private readonly RockResult _r;

    internal ScriptPit(ScriptBrushQuery brushes, RockResult r, double radius, long seed)
    {
        Brushes = brushes;
        _r = r;
        Radius = radius;
        Seed = seed;
    }

    [ScriptDoc("The air brushes (brush_query, top to bottom).", Since = 2)]
    public ScriptBrushQuery Brushes { get; }

    [ScriptDoc("The pit's flat floor height, metres.", Since = 2)]
    public double FloorY => _r.FloorY;

    [ScriptDoc("The bounds minimum (vec).", Since = 2)]
    public Vec3 Min => ScriptOrganic.ToVec3(_r.Min);

    [ScriptDoc("The bounds maximum (vec).", Since = 2)]
    public Vec3 Max => ScriptOrganic.ToVec3(_r.Max);

    [ScriptDoc("The floor point the pit opens from (vec, snapped).", Since = 2)]
    public Vec3 Center => ScriptOrganic.ToVec3(_r.Center);

    [ScriptDoc("The top radius, metres.", Since = 2)]
    public double Radius { get; }

    [ScriptDoc("The depth below the centre's floor, metres (rounded to 1/16).", Since = 2)]
    public double Depth => _r.Depth;

    [ScriptDoc("The seed the pit was built with.", Since = 2)]
    public double Seed { get; }

    public override string ToString() => $"pit({Brushes.Count} brushes, {Depth:0.##} m deep)";
}

internal static class ScriptOrganic
{
    internal static Vec3 ToVec3(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
}

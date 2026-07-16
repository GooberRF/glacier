namespace Ged.Core.Model;

/// <summary>A named waypoint list (RFL <c>waypoint_list</c>).</summary>
public sealed class WaypointList
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Indices into the nav-points array.</summary>
    public List<int> WaypointIndices { get; set; } = new();
}

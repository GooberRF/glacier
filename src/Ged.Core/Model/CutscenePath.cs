namespace Ged.Core.Model;

/// <summary>A cutscene camera path (RFL <c>cutscene_path</c>).</summary>
public sealed class CutscenePath
{
    public string Name { get; set; } = string.Empty;

    /// <summary>UIDs of the cutscene path nodes making up this path.</summary>
    public List<int> PathNodes { get; set; } = new();
}

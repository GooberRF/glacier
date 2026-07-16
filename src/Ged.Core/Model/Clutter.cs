namespace Ged.Core.Model;

/// <summary>A clutter object (RFL <c>clutter</c>).</summary>
public sealed class Clutter
{
    public ObjectHeader Header { get; set; } = new();

    /// <summary>rfl.ksy <c>unknown</c> (typically 0, unused by the engine). Preserved exactly.</summary>
    public int Unknown { get; set; }

    public string Skin { get; set; } = string.Empty;

    public List<int> Links { get; set; } = new();
}

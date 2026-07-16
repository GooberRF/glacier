namespace Ged.Core.Model;

/// <summary>A pickup item (RFL <c>item</c>).</summary>
public sealed class Item
{
    public ObjectHeader Header { get; set; } = new();

    public int Count { get; set; }

    public int RespawnTime { get; set; }

    public int TeamId { get; set; }
}

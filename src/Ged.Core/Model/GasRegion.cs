namespace Ged.Core.Model;

/// <summary>A gas / volumetric-fog region (RFL <c>gas_region</c>).</summary>
public sealed class GasRegion
{
    public ObjectHeader Header { get; set; } = new();

    /// <summary>1 = sphere, 2 = box.</summary>
    public int Shape { get; set; }

    public float? Radius { get; set; }

    public float? Height { get; set; }

    public float? Width { get; set; }

    public float? Depth { get; set; }

    public RfColor GasColor { get; set; }

    public float GasDensity { get; set; }
}

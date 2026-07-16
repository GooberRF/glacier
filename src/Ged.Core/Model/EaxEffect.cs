namespace Ged.Core.Model;

/// <summary>An EAX environmental-audio effect zone (RFL <c>eax_effect</c>).</summary>
public sealed class EaxEffect
{
    public string EffectType { get; set; } = string.Empty;

    public ObjectHeader Header { get; set; } = new();
}

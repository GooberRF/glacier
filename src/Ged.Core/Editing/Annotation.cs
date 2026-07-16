using System.Globalization;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// An editor-only measurement/dimension annotation (feature 4 / B7): a line between two
/// world points with endpoint ticks and a distance label. Stored in the .gedlayout.json
/// sidecar (never the RFL, so it is excluded from packfiles). The <see cref="Label"/> is
/// the free-text override; when null the annotation shows its formatted distance.
/// </summary>
public sealed class Annotation
{
    /// <summary>Stable per-document id (assigned by the owning <see cref="AnnotationList"/>).</summary>
    public int Id { get; set; }

    /// <summary>First measured world point.</summary>
    public Vec3 A { get; set; }

    /// <summary>Second measured world point.</summary>
    public Vec3 B { get; set; }

    /// <summary>Optional free-text label; null → the formatted distance is shown.</summary>
    public string? Label { get; set; }

    /// <summary>The measured distance between the endpoints (metres).</summary>
    public float Distance => B.Sub(A).Length();

    /// <summary>The text drawn on the label billboard: the override, else the formatted distance.</summary>
    public string EffectiveLabel =>
        Label ?? Distance.ToString("0.##", CultureInfo.InvariantCulture) + " m";

    public Annotation Clone() => new() { Id = Id, A = A, B = B, Label = Label };
}

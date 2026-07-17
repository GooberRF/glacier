using System.Reflection;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Reflection-based get/set of a level-object model's orientation. Object models expose their
/// orientation under one of two property names (<c>Rotation</c> or <c>Orientation</c>), typed
/// either <see cref="Mat3"/> or <see cref="System.Nullable{Mat3}"/>. This is the one shared
/// accessor for whole-object rotation (the prefab-instance unit transform, and mirrors the
/// pattern used by the group / gizmo object paths), so the reflection lives in exactly one place.
/// </summary>
public static class ObjectRotation
{
    /// <summary>The model's orientation, or null when it has no rotation property.</summary>
    public static Mat3? Get(object model)
    {
        PropertyInfo? p = model.GetType().GetProperty("Rotation") ?? model.GetType().GetProperty("Orientation");
        if (p is null)
        {
            return null;
        }

        if (p.PropertyType == typeof(Mat3))
        {
            return (Mat3)p.GetValue(model)!;
        }

        return p.PropertyType == typeof(Mat3?) && p.GetValue(model) is Mat3 m ? m : null;
    }

    /// <summary>Sets the model's orientation; no-op when it has no writable rotation property.</summary>
    public static void Set(object model, Mat3 value)
    {
        PropertyInfo? p = model.GetType().GetProperty("Rotation") ?? model.GetType().GetProperty("Orientation");
        if (p is null || !p.CanWrite)
        {
            return;
        }

        if (p.PropertyType == typeof(Mat3))
        {
            p.SetValue(model, value);
        }
        else if (p.PropertyType == typeof(Mat3?))
        {
            p.SetValue(model, (Mat3?)value);
        }
    }
}

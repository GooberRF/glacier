using System.Reflection;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Editing;

/// <summary>
/// Reflection-based get/set of a level-object model's orientation. Object models expose their
/// orientation under one of two property names (<c>Rotation</c> or <c>Orientation</c>), typed
/// either <see cref="Mat3"/> or <see cref="System.Nullable{Mat3}"/>; header-based models (items,
/// clutter, emitters, decals, regions …) keep it in <see cref="ObjectHeader.Rotation"/>, which is
/// used when the model itself has no rotation property. This is the one shared accessor for
/// whole-object rotation (the prefab-instance unit transform, the script API, and mirrors the
/// pattern used by the group / gizmo object paths), so the reflection lives in exactly one place.
/// </summary>
public static class ObjectRotation
{
    /// <summary>The model's orientation, or null when it has no rotation property (or a null conditional one).</summary>
    public static Mat3? Get(object model)
    {
        if (!TryProperty(model, out object owner, out PropertyInfo? p))
        {
            return null;
        }

        if (p.PropertyType == typeof(Mat3))
        {
            return (Mat3)p.GetValue(owner)!;
        }

        return p.PropertyType == typeof(Mat3?) && p.GetValue(owner) is Mat3 m ? m : null;
    }

    /// <summary>Sets the model's orientation; no-op when it has no writable rotation property.</summary>
    public static void Set(object model, Mat3 value)
    {
        if (!TryProperty(model, out object owner, out PropertyInfo? p) || !p.CanWrite)
        {
            return;
        }

        if (p.PropertyType == typeof(Mat3))
        {
            p.SetValue(owner, value);
        }
        else if (p.PropertyType == typeof(Mat3?))
        {
            p.SetValue(owner, (Mat3?)value);
        }
    }

    /// <summary>
    /// The orientation the level would save for <paramref name="model"/>, honouring conditional
    /// storage (<see cref="InspectorPresence"/>): a sphere trigger, a non-directional nav point or
    /// an unoriented event class has none (<paramref name="hint"/> says what to change first); a
    /// present-but-unset conditional orientation reads as identity, exactly as the writer saves it.
    /// Returns false (hint null) when the model type has no orientation at all.
    /// </summary>
    public static bool TryGetOrientation(object model, out Mat3 rotation, out string? hint)
    {
        rotation = Mat3.Identity;
        hint = null;
        if (!TryProperty(model, out object owner, out PropertyInfo? p))
        {
            return false;
        }

        if (owner == model && InspectorPresence.Find(model, p.Name) is { } conditional && !conditional.IsPresent(model))
        {
            hint = conditional.Hint;
            return false;
        }

        rotation = Get(model) ?? Mat3.Identity;
        return true;
    }

    /// <summary>The raw rotation value (for an exact undo): the property value, possibly null.</summary>
    public static object? GetRaw(object model) =>
        TryProperty(model, out object owner, out PropertyInfo? p) ? p.GetValue(owner) : null;

    /// <summary>Restores a value captured by <see cref="GetRaw"/>.</summary>
    public static void SetRaw(object model, object? raw)
    {
        if (TryProperty(model, out object owner, out PropertyInfo? p) && p.CanWrite)
        {
            p.SetValue(owner, raw);
        }
    }

    private static bool TryProperty(object model, out object owner, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PropertyInfo? p)
    {
        owner = model;
        p = model.GetType().GetProperty("Rotation") ?? model.GetType().GetProperty("Orientation");
        if (p is not null && (p.PropertyType == typeof(Mat3) || p.PropertyType == typeof(Mat3?)))
        {
            return true;
        }

        // Header-based models keep their orientation in the shared ObjectHeader.
        if (model.GetType().GetProperty("Header")?.GetValue(model) is ObjectHeader header)
        {
            owner = header;
            p = typeof(ObjectHeader).GetProperty(nameof(ObjectHeader.Rotation));
            return p is not null;
        }

        p = null;
        return false;
    }
}

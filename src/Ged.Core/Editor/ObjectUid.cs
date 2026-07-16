using System.Reflection;

namespace Ged.Core.Editor;

/// <summary>
/// Reads and writes the UID of an arbitrary RFL model object without knowing its
/// concrete type: a top-level <c>Uid</c> property when present, otherwise the
/// <c>Uid</c> of a nested <c>Header</c> (<see cref="Ged.Core.Model.ObjectHeader"/>).
/// </summary>
public static class ObjectUid
{
    public static bool TryGet(object model, out int uid)
    {
        uid = 0;
        PropertyInfo? direct = model.GetType().GetProperty("Uid", BindingFlags.Public | BindingFlags.Instance);
        if (direct is not null && direct.PropertyType == typeof(int))
        {
            uid = (int)direct.GetValue(model)!;
            return true;
        }

        object? header = model.GetType().GetProperty("Header", BindingFlags.Public | BindingFlags.Instance)?.GetValue(model);
        if (header is not null)
        {
            PropertyInfo? hu = header.GetType().GetProperty("Uid");
            if (hu is not null && hu.PropertyType == typeof(int))
            {
                uid = (int)hu.GetValue(header)!;
                return true;
            }
        }

        return false;
    }

    public static void Set(object model, int uid)
    {
        PropertyInfo? direct = model.GetType().GetProperty("Uid", BindingFlags.Public | BindingFlags.Instance);
        if (direct is not null && direct.CanWrite && direct.PropertyType == typeof(int))
        {
            direct.SetValue(model, uid);
            return;
        }

        object? header = model.GetType().GetProperty("Header", BindingFlags.Public | BindingFlags.Instance)?.GetValue(model);
        header?.GetType().GetProperty("Uid")?.SetValue(header, uid);
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Ged.Core.Editor;

/// <summary>
/// A small reflection-based deep cloner for the plain-data RFL model objects.
/// Value types and strings are copied by value; lists and arrays are rebuilt with
/// cloned elements; nested model classes are cloned recursively. Used by
/// copy/paste so a duplicated object shares no mutable state with its original.
/// </summary>
public static class ModelCloner
{
    /// <summary>Deep-clones a model object (must be a class with a public parameterless constructor).</summary>
    public static object Clone(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CloneValue(source, source.GetType())!;
    }

    private static object? CloneValue(object? value, Type declaredType)
    {
        if (value is null)
        {
            return null;
        }

        Type type = value.GetType();
        if (type.IsValueType || value is string)
        {
            return value;
        }

        if (value is Array array)
        {
            var copy = (Array)array.Clone();
            for (int i = 0; i < copy.Length; i++)
            {
                object? element = copy.GetValue(i);
                if (element is not null && !element.GetType().IsValueType && element is not string)
                {
                    copy.SetValue(CloneValue(element, element.GetType()), i);
                }
            }

            return copy;
        }

        if (value is IList list)
        {
            var clone = (IList)Activator.CreateInstance(type)!;
            foreach (object? element in list)
            {
                clone.Add(CloneValue(element, element?.GetType() ?? typeof(object)));
            }

            return clone;
        }

        object instance = Activator.CreateInstance(type)!;
        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? sourceValue = prop.GetValue(value);
            if (prop.CanWrite)
            {
                prop.SetValue(instance, CloneValue(sourceValue, prop.PropertyType));
            }
            else if (sourceValue is IList sourceList && prop.GetValue(instance) is IList targetList)
            {
                targetList.Clear();
                foreach (object? element in sourceList)
                {
                    targetList.Add(CloneValue(element, element?.GetType() ?? typeof(object)));
                }
            }
        }

        return instance;
    }
}

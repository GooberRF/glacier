using System;
using System.Collections;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;

namespace Ged.Core.Editor;

/// <summary>
/// A uniform, delegate-backed handle onto one level object regardless of its
/// underlying RFL model type. Exposes the common fields (uid, script name,
/// position, hidden flag, class) plus its owning collection so selection,
/// visibility, the outliner, the property grid and copy/paste can all work
/// against a single abstraction. Editing any exposed field marks the owning
/// <see cref="Section"/> dirty via <see cref="MarkDirty"/>.
/// </summary>
public sealed class LevelObject
{
    private readonly IList? _list;
    private readonly Func<int> _getUid;
    private readonly Action<int> _setUid;
    private readonly Func<string> _getScript;
    private readonly Action<string> _setScript;
    private readonly Func<Vec3> _getPos;
    private readonly Action<Vec3> _setPos;
    private readonly Func<bool> _getHidden;
    private readonly Action<bool> _setHidden;
    private readonly Func<string> _getClass;

    internal LevelObject(
        LevelObjectKind kind,
        RflSection section,
        object model,
        IList? owningList,
        Func<int> getUid,
        Action<int> setUid,
        Func<string> getScript,
        Action<string> setScript,
        Func<Vec3> getPos,
        Action<Vec3> setPos,
        Func<bool> getHidden,
        Action<bool> setHidden,
        Func<string> getClass)
    {
        Kind = kind;
        Section = section;
        Model = model;
        _list = owningList;
        _getUid = getUid;
        _setUid = setUid;
        _getScript = getScript;
        _setScript = setScript;
        _getPos = getPos;
        _setPos = setPos;
        _getHidden = getHidden;
        _setHidden = setHidden;
        _getClass = getClass;
    }

    public LevelObjectKind Kind { get; }

    /// <summary>The RFL section that owns this object (marked dirty when it changes).</summary>
    public RflSection Section { get; }

    /// <summary>The underlying model instance (an <see cref="Entity"/>, <see cref="Light"/>, ...).</summary>
    public object Model { get; }

    public int Uid
    {
        get => _getUid();
        set
        {
            _setUid(value);
            MarkDirty();
        }
    }

    public string ScriptName
    {
        get => _getScript();
        set
        {
            _setScript(value);
            MarkDirty();
        }
    }

    public Vec3 Position
    {
        get => _getPos();
        set
        {
            _setPos(value);
            MarkDirty();
        }
    }

    public bool Hidden
    {
        get => _getHidden();
        set
        {
            _setHidden(value);
            MarkDirty();
        }
    }

    public string ClassName => _getClass();

    /// <summary>True when this object supports copy/paste and delete (list-backed).</summary>
    public bool CanRemove => _list is not null;

    /// <summary>Index of this object within its owning collection, or -1.</summary>
    public int IndexInSection => _list?.IndexOf(Model) ?? -1;

    /// <summary>The owning collection (for copy/paste that appends foreign clones).</summary>
    internal IList? OwningList => _list;

    /// <summary>A friendly label: the script name, else class name, else kind + uid.</summary>
    public string DisplayName
    {
        get
        {
            string script = ScriptName;
            if (!string.IsNullOrEmpty(script))
            {
                return script;
            }

            string cls = ClassName;
            return !string.IsNullOrEmpty(cls) ? cls : $"{Kind} {Uid}";
        }
    }

    /// <summary>Sets the UID without marking dirty (used when preparing a fresh clone).</summary>
    public void SetUidRaw(int uid) => _setUid(uid);

    /// <summary>Flags the owning section for re-serialization on save.</summary>
    public void MarkDirty() => Section.Dirty = true;

    /// <summary>Deep-clones the underlying model (for copy/paste).</summary>
    public object CloneModel() => ModelCloner.Clone(Model);

    /// <summary>Appends a same-typed model (a clone) to this object's owning collection.</summary>
    public void AppendToSection(object model)
    {
        Require().Add(model);
        MarkDirty();
    }

    /// <summary>Removes this object from its owning collection.</summary>
    public void RemoveFromSection()
    {
        Require().Remove(Model);
        MarkDirty();
    }

    /// <summary>Re-inserts a model at a specific index (undo of a delete).</summary>
    public void InsertIntoSection(int index, object model)
    {
        IList list = Require();
        list.Insert(Math.Clamp(index, 0, list.Count), model);
        MarkDirty();
    }

    private IList Require() =>
        _list ?? throw new InvalidOperationException($"{Kind} is not a list-backed object.");
}

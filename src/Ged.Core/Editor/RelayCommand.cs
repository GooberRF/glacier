using System;

namespace Ged.Core.Editor;

/// <summary>
/// A command defined by two delegates. The workhorse used for property edits,
/// selection changes, adds/removes — anything expressible as a do/undo pair.
/// </summary>
public sealed class RelayCommand : IDocumentCommand
{
    private readonly Action _do;
    private readonly Action _undo;

    public RelayCommand(string description, Action doAction, Action undoAction, string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(doAction);
        ArgumentNullException.ThrowIfNull(undoAction);
        Description = description;
        _do = doAction;
        _undo = undoAction;
        CoalesceKey = coalesceKey;
    }

    public string Description { get; }

    public string? CoalesceKey { get; }

    public void Do() => _do();

    public void Undo() => _undo();
}

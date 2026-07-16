using System;
using System.Collections.Generic;

namespace Ged.App.Services;

/// <summary>
/// Tracks the set of long-running editor operations currently in flight (item 3). Operations
/// call <see cref="Begin"/> to register (getting back a handle they <see cref="OperationProgress.Report(string?, int, int)"/>
/// against and dispose when finished); the progress overlay observes <see cref="Changed"/> and
/// shows one small card per active operation, stacking when several overlap and hiding when the
/// set empties. Purely a model — no UI, no threading assumptions beyond that observers marshal
/// <see cref="Changed"/> to the UI thread if they touch controls.
/// </summary>
internal sealed class OperationProgressService
{
    private readonly List<OperationProgress> _ops = new();

    public IReadOnlyList<OperationProgress> Operations => _ops;

    /// <summary>Raised whenever an operation starts, reports progress, or finishes.</summary>
    public event Action? Changed;

    /// <summary>Registers a new active operation and returns its handle (dispose to finish it).</summary>
    public OperationProgress Begin(string name)
    {
        var op = new OperationProgress(name, this);
        _ops.Add(op);
        Changed?.Invoke();
        return op;
    }

    internal void End(OperationProgress op)
    {
        if (_ops.Remove(op))
        {
            Changed?.Invoke();
        }
    }

    internal void Raise() => Changed?.Invoke();
}

/// <summary>A single in-flight operation: its display name and latest progress state.</summary>
internal sealed class OperationProgress : IDisposable
{
    private readonly OperationProgressService _service;
    private bool _ended;

    internal OperationProgress(string name, OperationProgressService service)
    {
        Name = name;
        _service = service;
    }

    public string Name { get; }

    /// <summary>Sub-line detail (e.g. the current stage), or null.</summary>
    public string? Detail { get; private set; }

    /// <summary>Determinate fraction 0..1, or null for an indeterminate (spinner) operation.</summary>
    public double? Fraction { get; private set; }

    /// <summary>Reports staged progress; a positive total makes the bar determinate.</summary>
    public void Report(string? stage, int current, int total)
    {
        Detail = total > 0 ? $"{stage} {current}/{total}" : stage;
        Fraction = total > 0 ? Math.Clamp((double)current / total, 0, 1) : null;
        _service.Raise();
    }

    /// <summary>Reports an indeterminate operation with an optional detail line.</summary>
    public void ReportIndeterminate(string? detail)
    {
        Detail = detail;
        Fraction = null;
        _service.Raise();
    }

    public void Dispose()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        _service.End(this);
    }
}

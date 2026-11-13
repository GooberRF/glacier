using System;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation;

namespace Ged.Cli.Mcp;

/// <summary>
/// One tool call touches the document at a time: parallel calls from a host queue in arrival order
/// (<see cref="SemaphoreSlim"/> waiters are released first-in, first-out) and a call that waits longer than
/// <see cref="QueueTimeout"/> is answered <c>busy</c>.
/// </summary>
internal sealed class CallGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How long a call may wait for the one before it.</summary>
    public TimeSpan QueueTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Waits for the gate; the returned handle releases it. Throws <c>busy</c> after <see cref="QueueTimeout"/>.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellation)
    {
        if (!await _gate.WaitAsync(QueueTimeout, cancellation).ConfigureAwait(false))
        {
            throw new AutomationException(AutomationErrorCodes.Busy,
                $"Another tool call has been running for over {QueueTimeout.TotalSeconds:0} s; try again when it finishes.");
        }

        return new Releaser(_gate);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

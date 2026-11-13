using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ged.Automation;

/// <summary>
/// The client side of the editor bridge (<see cref="BridgeProtocol"/>): connects to the running
/// editor's pipe and exchanges request/response lines. Used by <c>glacier-cli --attach</c> and by the
/// MCP server attached to the editor. The optional features (progress lines, cancel) are used only after a
/// <c>hello</c> on this connection advertised them, so the client also works with editors that predate them.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    private readonly Stream _stream;
    private readonly FrameReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private HashSet<string> _capabilities = new(StringComparer.Ordinal);
    private int _nextId = 1;
    private volatile bool _broken;

    /// <summary>Wraps an already-connected stream (tests use an in-memory duplex).</summary>
    public BridgeClient(Stream connected)
    {
        _stream = connected ?? throw new ArgumentNullException(nameof(connected));
        _reader = new FrameReader(connected);
    }

    /// <summary>
    /// The optional features the editor advertised in its last <c>hello</c> on this connection
    /// (<see cref="BridgeCapabilities"/>); empty before a <c>hello</c> or with an editor that has none.
    /// </summary>
    public IReadOnlySet<string> Capabilities => _capabilities;

    /// <summary>
    /// True once the connection can no longer be used: the editor closed it, or a request was abandoned
    /// mid-reply (a cancel the editor could not be told about, or one it did not answer in time).
    /// </summary>
    public bool IsBroken => _broken;

    /// <summary>How long a canceled request waits for the editor's <c>canceled</c> reply before the connection is abandoned.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Connects to the editor's pipe, or returns null when no editor answers within
    /// <paramref name="timeout"/> (not running, bridge disabled, or another client is connected).
    /// </summary>
    public static async Task<BridgeClient?> TryConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)Math.Max(1, timeout.TotalMilliseconds), cancellation).ConfigureAwait(false);
            return new BridgeClient(pipe);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Sends <c>hello</c> (which also records the editor's <see cref="Capabilities"/>).</summary>
    public Task<AutomationResponse> HelloAsync(CancellationToken cancellation = default) =>
        SendAsync(new AutomationRequest("hello"), progress: null, cancellation);

    /// <summary>Sends one request and waits for its response.</summary>
    public Task<AutomationResponse> SendAsync(AutomationRequest request, CancellationToken cancellation = default) =>
        SendAsync(request, progress: null, cancellation);

    /// <summary>
    /// Sends one request and waits for its response. When the editor advertised
    /// <see cref="BridgeCapabilities.Progress"/> and <paramref name="progress"/> is given, interim progress lines
    /// are reported to it (on this method's continuation thread). When the editor advertised
    /// <see cref="BridgeCapabilities.Cancel"/>, canceling <paramref name="cancellation"/> sends a cancel line and
    /// waits (up to <see cref="CancelGrace"/>) for the editor's <c>canceled</c> answer, which is returned like any
    /// other response; otherwise canceling abandons the reply and leaves the connection <see cref="IsBroken"/>.
    /// </summary>
    /// <exception cref="IOException">The editor closed the connection.</exception>
    /// <exception cref="OperationCanceledException">Canceled without a reply from the editor.</exception>
    public async Task<AutomationResponse> SendAsync(AutomationRequest request, IProgress<AutomationProgress>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_broken)
        {
            throw new IOException("The editor connection is no longer usable.");
        }

        int id = _nextId++;
        bool wantProgress = progress is not null && _capabilities.Contains(BridgeCapabilities.Progress);
        bool canCancel = _capabilities.Contains(BridgeCapabilities.Cancel);
        string line = BridgeProtocol.SerializeRequest(id, request, wantProgress);
        if (System.Text.Encoding.UTF8.GetByteCount(line) > BridgeProtocol.MaxMessageBytes)
        {
            throw new AutomationException(AutomationErrorCodes.Protocol,
                $"The request exceeds the {BridgeProtocol.MaxMessageBytes / (1024 * 1024)} MB limit.");
        }

        cancellation.ThrowIfCancellationRequested();
        try
        {
            await WriteAsync(line, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _broken = true;
            throw new IOException("The editor closed the connection.", ex);
        }

        // With cancel support the reply is awaited even after cancellation (the editor answers "canceled"),
        // bounded by the grace period; without it, canceling abandons the read (and so the connection).
        using var readCts = canCancel ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using CancellationTokenRegistration onCancel = canCancel
            ? cancellation.Register(() =>
            {
                _ = SendCancelAsync(id);
                try
                {
                    readCts.CancelAfter(CancelGrace);
                }
                catch (ObjectDisposedException)
                {
                    // the reply already arrived
                }
            })
            : default;

        try
        {
            while (true)
            {
                (FrameStatus status, string? reply) = await _reader.ReadAsync(readCts.Token).ConfigureAwait(false);
                if (status == FrameStatus.TooLarge)
                {
                    throw new AutomationException(AutomationErrorCodes.Protocol, "The editor's response was too large.");
                }

                if (status != FrameStatus.Line)
                {
                    _broken = true;
                    throw new IOException("The editor closed the connection.");
                }

                JsonObject? o = TryParseObject(reply!);
                if (o is not null && BridgeProtocol.TryParseProgress(o, out AutomationProgress p))
                {
                    if (wantProgress)
                    {
                        progress!.Report(p);
                    }

                    continue;
                }

                AutomationResponse response = BridgeProtocol.ParseResponse(reply!);
                if (request.Command == "hello" && response.Ok)
                {
                    _capabilities = ReadCapabilities(response.Result);
                }

                return response;
            }
        }
        catch (OperationCanceledException)
        {
            _broken = true; // the reply (if any) is still in flight: this connection is out of step
            throw new OperationCanceledException("The request was canceled before the editor answered.", cancellation.IsCancellationRequested ? cancellation : readCts.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _broken = true;
            throw ex as IOException ?? new IOException("The editor closed the connection.", ex);
        }
    }

    private async Task SendCancelAsync(int id)
    {
        try
        {
            await WriteAsync(BridgeProtocol.SerializeCancel(id), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection is closing; the read side reports it.
        }
    }

    private async Task WriteAsync(string line, CancellationToken cancellation)
    {
        await _writeLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await BridgeProtocol.WriteLineAsync(_stream, line, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static JsonObject? TryParseObject(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null; // ParseResponse reports it
        }
    }

    private static HashSet<string> ReadCapabilities(JsonNode? hello)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (hello?["capabilities"] is JsonArray list)
        {
            foreach (JsonNode? c in list)
            {
                if (c is JsonValue v && v.TryGetValue(out string? name) && !string.IsNullOrEmpty(name))
                {
                    set.Add(name);
                }
            }
        }

        return set;
    }

    public void Dispose()
    {
        _broken = true;
        _stream.Dispose();
        _writeLock.Dispose();
    }
}

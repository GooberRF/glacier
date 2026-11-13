using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Ged.Automation;

/// <summary>
/// The named-pipe endpoint that lets <c>glacier-cli --attach</c> and <c>glacier-cli mcp</c> drive a
/// running editor. It owns the pipe lifecycle and the wire protocol (<see cref="BridgeProtocol"/>) and
/// hands each parsed request to a handler — the editor marshals it onto its UI thread and executes it
/// through <see cref="AutomationCommands"/>. One client at a time; requests on a connection run
/// strictly in order (a running request can be canceled, and can stream progress lines, when the client
/// asks — <see cref="BridgeCapabilities"/>). The pipe is restricted to the current user.
/// </summary>
public sealed class BridgeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<AutomationRequest, IProgress<AutomationProgress>?, CancellationToken, Task<AutomationResponse>> _handler;
    private readonly int _maxMessageBytes;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeServerStream? _pending;
    private Task? _loop;

    /// <param name="pipeName">The pipe name (normally <see cref="BridgeProtocol.PipeName"/>).</param>
    /// <param name="handler">Executes one request (called sequentially, never concurrently).</param>
    /// <param name="maxMessageBytes">The request size guard.</param>
    public BridgeServer(
        string pipeName,
        Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> handler,
        int maxMessageBytes = BridgeProtocol.MaxMessageBytes)
        : this(pipeName, WithoutProgress(handler), maxMessageBytes)
    {
    }

    /// <param name="pipeName">The pipe name (normally <see cref="BridgeProtocol.PipeName"/>).</param>
    /// <param name="handler">Executes one request (called sequentially, never concurrently). Its progress sink is
    /// non-null when the client asked for progress lines, and may be called from any thread.</param>
    /// <param name="maxMessageBytes">The request size guard.</param>
    public BridgeServer(
        string pipeName,
        Func<AutomationRequest, IProgress<AutomationProgress>?, CancellationToken, Task<AutomationResponse>> handler,
        int maxMessageBytes = BridgeProtocol.MaxMessageBytes)
    {
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _maxMessageBytes = maxMessageBytes;
    }

    /// <summary>The fewest milliseconds between two progress lines of one request (a stage change always reports).</summary>
    public const int ProgressIntervalMs = 100;

    private static Func<AutomationRequest, IProgress<AutomationProgress>?, CancellationToken, Task<AutomationResponse>> WithoutProgress(
        Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (request, _, ct) => handler(request, ct);
    }

    /// <summary>Raised (on a worker thread) when a client connects (true) or disconnects (false).</summary>
    public event Action<bool>? ClientConnectionChanged;

    /// <summary>Raised (on a worker thread) for unexpected connection failures.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>The pipe name served.</summary>
    public string PipeName => _pipeName;

    /// <summary>True once <see cref="TryStart"/> succeeded and until disposal.</summary>
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>
    /// Claims the pipe name and starts serving. Returns false (with a reason) when another process —
    /// typically a second editor instance — already serves this name; the caller then runs without a
    /// bridge.
    /// </summary>
    public bool TryStart(out string? reason)
    {
        reason = null;
        if (_loop is not null)
        {
            return true;
        }

        // A live server answers a connect immediately. (On Unix a stale socket file would otherwise be
        // replaced, stealing the name from the first editor; on Windows the instance limit also guards.)
        if (IsServedElsewhere(_pipeName))
        {
            reason = "another Glacier window is already accepting command-line connections";
            return false;
        }

        try
        {
            _pending = CreateInstance();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"the command-line pipe is unavailable ({ex.Message})";
            return false;
        }

        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return true;
    }

    private NamedPipeServerStream CreateInstance() => new(
        _pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static bool IsServedElsewhere(string pipeName)
    {
        try
        {
            using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            probe.Connect(100);
            return true;
        }
        catch (Exception)
        {
            return false; // timeout / not found → free
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = _pending ?? CreateInstance();
                _pending = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Faulted?.Invoke(ex);
                try
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }
            catch (Exception)
            {
                return;
            }

            await using (server.ConfigureAwait(false))
            {
                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    continue;
                }

                ClientConnectionChanged?.Invoke(true);
                try
                {
                    await ServeAsync(server, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // The client went away (or we are shutting down).
                }
                catch (Exception ex)
                {
                    Faulted?.Invoke(ex);
                }
                finally
                {
                    ClientConnectionChanged?.Invoke(false);
                }
            }
        }
    }

    /// <summary>
    /// Serves one connection until the client closes it. Requests run one at a time; while one runs the
    /// connection keeps reading, so a <c>cancel</c> line can stop it (any other request is answered
    /// <c>busy</c>). Every outgoing line — interim progress and responses — goes through one ordered writer.
    /// </summary>
    internal async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var reader = new FrameReader(stream, _maxMessageBytes);
        Channel<string> outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        Task writer = WriteLoopAsync(stream, outbox.Reader, ct);

        // Provenance: every script a connection runs shares one ai.session id (unless the request names
        // its own), so an agent's edits over one connection can be found together later.
        string session = AutomationCommands.NewSessionId();
        Task<(FrameStatus Status, string? Line)>? pendingRead = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Task<(FrameStatus Status, string? Line)> read = pendingRead ?? reader.ReadAsync(ct);
                pendingRead = null;
                (FrameStatus status, string? line) = await ReadOrWriterFailureAsync(read, writer).ConfigureAwait(false);
                if (status == FrameStatus.EndOfStream)
                {
                    return;
                }

                if (status == FrameStatus.TooLarge)
                {
                    outbox.Writer.TryWrite(BridgeProtocol.SerializeResponse(null, Error(string.Empty,
                        $"Request exceeds the {_maxMessageBytes / (1024 * 1024)} MB limit.")));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line) || BridgeProtocol.TryParseCancel(line!, out _))
                {
                    continue; // (a cancel for a request that has already answered needs nothing)
                }

                ParsedRequest parsed = ParsedRequest.Parse(line!);
                if (parsed.Request is not { } request)
                {
                    outbox.Writer.TryWrite(BridgeProtocol.SerializeResponse(parsed.Id, new AutomationResponse { Ok = false, Error = parsed.Error }));
                    continue;
                }

                if (request.Command == "run" && !request.Args.ContainsKey("session"))
                {
                    request.Args["session"] = session;
                }

                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ProgressLines? sink = parsed.WantsProgress ? new ProgressLines(parsed.Id, outbox.Writer) : null;
                // Off this loop's thread: a handler that runs inline (a headless host) must not stop the reading
                // of a cancel line.
                CancellationToken requestToken = requestCts.Token;
                Task<AutomationResponse> running = Task.Run(() => RunHandlerAsync(request, sink, requestToken), CancellationToken.None);

                // While the request runs, keep reading: a cancel line for it stops it; another request is busy.
                while (!running.IsCompleted)
                {
                    pendingRead ??= reader.ReadAsync(ct);
                    Task first = await Task.WhenAny(running, pendingRead, writer).ConfigureAwait(false);
                    if (first == writer)
                    {
                        requestCts.Cancel();
                        await writer.ConfigureAwait(false); // surfaces the write failure
                        return;
                    }

                    if (first == running)
                    {
                        break;
                    }

                    (FrameStatus midStatus, string? midLine) = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                    if (midStatus == FrameStatus.EndOfStream)
                    {
                        // The client went away mid-request: stop the request, then drop the connection.
                        requestCts.Cancel();
                        await IgnoreFailureAsync(running).ConfigureAwait(false);
                        return;
                    }

                    if (midStatus == FrameStatus.Line && string.IsNullOrWhiteSpace(midLine))
                    {
                        continue;
                    }

                    if (midStatus == FrameStatus.Line && BridgeProtocol.TryParseCancel(midLine!, out JsonNode? cancelId))
                    {
                        if (BridgeProtocol.SameId(cancelId, parsed.Id))
                        {
                            requestCts.Cancel();
                        }

                        continue;
                    }

                    JsonNode? otherId = midStatus == FrameStatus.Line ? ParsedRequest.Parse(midLine!).Id : null;
                    outbox.Writer.TryWrite(BridgeProtocol.SerializeResponse(otherId, new AutomationResponse
                    {
                        Ok = false,
                        Command = string.Empty,
                        Error = new AutomationError(AutomationErrorCodes.Busy,
                            "Another request on this connection is still running; send one request at a time."),
                    }));
                }

                AutomationResponse response = await running.ConfigureAwait(false);
                sink?.Close(); // no progress line after the response
                if (request.Command == "hello" && response.Ok && response.Result is JsonObject hello)
                {
                    hello["capabilities"] = new JsonArray(BridgeCapabilities.All.Select(c => (JsonNode?)c).ToArray());
                }

                outbox.Writer.TryWrite(BridgeProtocol.SerializeResponse(parsed.Id, response));
            }
        }
        finally
        {
            if (pendingRead is { IsCompleted: false } orphan)
            {
                // Observe the read the closing stream will fault.
                _ = orphan.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            outbox.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false); // flush what is queued (or observe the write failure)
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // the client is gone
            }
        }
    }

    private async Task<AutomationResponse> RunHandlerAsync(AutomationRequest request, IProgress<AutomationProgress>? progress, CancellationToken ct)
    {
        try
        {
            return await _handler(request, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AutomationResponse
            {
                Ok = false,
                Command = request.Command,
                Error = new AutomationError(AutomationErrorCodes.Canceled, "The command was canceled before it finished; the level is unchanged by it."),
            };
        }
        catch (Exception ex)
        {
            return new AutomationResponse
            {
                Ok = false,
                Command = request.Command,
                Error = new AutomationError(AutomationErrorCodes.Internal, ex.Message),
            };
        }
    }

    private static async Task<(FrameStatus Status, string? Line)> ReadOrWriterFailureAsync(Task<(FrameStatus Status, string? Line)> read, Task writer)
    {
        if (await Task.WhenAny(read, writer).ConfigureAwait(false) == writer)
        {
            _ = read.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await writer.ConfigureAwait(false); // a write failed: surface it (ends the connection)
            return (FrameStatus.EndOfStream, null);
        }

        return await read.ConfigureAwait(false);
    }

    private static async Task WriteLoopAsync(Stream stream, ChannelReader<string> lines, CancellationToken ct)
    {
        await foreach (string line in lines.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await BridgeProtocol.WriteLineAsync(stream, line, ct).ConfigureAwait(false);
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the connection is closing; the request's outcome has nowhere to go
        }
    }

    /// <summary>
    /// The progress sink of one request: queues interim lines without ever blocking the reporting thread
    /// (compile progress arrives on worker threads), at most one every <see cref="ProgressIntervalMs"/> ms
    /// plus every stage change, and none once the response is queued.
    /// </summary>
    private sealed class ProgressLines : IProgress<AutomationProgress>
    {
        private readonly JsonNode? _id;
        private readonly ChannelWriter<string> _out;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _lock = new();
        private string? _stage;
        private long _last = -ProgressIntervalMs;
        private bool _closed;

        public ProgressLines(JsonNode? id, ChannelWriter<string> output)
        {
            _id = id?.DeepClone();
            _out = output;
        }

        public void Report(AutomationProgress value)
        {
            lock (_lock)
            {
                long now = _clock.ElapsedMilliseconds;
                if (_closed || (value.Stage == _stage && now - _last < ProgressIntervalMs))
                {
                    return;
                }

                _stage = value.Stage;
                _last = now;
                _out.TryWrite(BridgeProtocol.SerializeProgress(_id, value));
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
            }
        }
    }

    private static AutomationResponse Error(string command, string message) => new()
    {
        Ok = false,
        Command = command,
        Error = new AutomationError(AutomationErrorCodes.Protocol, message),
    };

    /// <summary>Stops serving and releases the pipe name.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _pending?.Dispose();
        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // shutting down
            }
        }

        _cts.Dispose();
    }
}

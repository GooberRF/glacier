using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ged.Automation;

/// <summary>
/// The editor bridge wire protocol: newline-delimited UTF-8 JSON over a per-user named pipe (a Unix
/// domain socket on Linux), one request line answered by one response line, any number of requests
/// per connection, one client at a time.
/// <code>
/// request : {"protocol":1,"id":7,"command":"run","args":{"script":"...","name":"a.lua"}}
/// response: {"protocol":1,"id":7,"ok":true,"command":"run","result":{...}}
///           {"protocol":1,"id":7,"ok":false,"command":"run","error":{"code":"...","message":"..."}}
/// </code>
/// <c>id</c> is echoed back verbatim (any JSON value). A request whose <c>protocol</c> differs from
/// <see cref="Version"/>, malformed JSON, and a line larger than <see cref="MaxMessageBytes"/> are
/// answered with a <c>protocol</c> error and the connection stays usable.
/// <para>
/// Optional features (additive within version 1), advertised in the <c>hello</c> result's
/// <c>capabilities</c> and used by a client only when advertised (<see cref="BridgeCapabilities"/>):
/// </para>
/// <code>
/// progress: request {"protocol":1,"id":7,"command":"build","args":{…},"progress":true}
///           interim {"protocol":1,"id":7,"progress":{"stage":"…","current":n,"total":m}}  (zero or more, then the response)
/// cancel  : {"protocol":1,"cancel":7}  while request 7 runs; no reply of its own — request 7 answers "canceled".
///           Any other line received while a request runs is answered "busy".
/// </code>
/// </summary>
public static class BridgeProtocol
{
    /// <summary>The protocol version spoken by this build.</summary>
    public const int Version = 1;

    /// <summary>The largest accepted message (one JSON line), in bytes.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The per-user pipe name. Fixed for a user (so the CLI finds the editor without configuration)
    /// and different between users on a shared machine; the pipe itself is also restricted to the
    /// current user (<c>PipeOptions.CurrentUserOnly</c>).
    /// </summary>
    public static string PipeName(string? userName = null)
    {
        string user = (userName ?? Environment.UserName ?? "user").ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(user));
        return "glacier-bridge-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Serializes a request line (without the trailing newline).</summary>
    /// <param name="id">The request id.</param>
    /// <param name="request">The request.</param>
    /// <param name="progress">Ask for interim progress lines (only when the server advertised <see cref="BridgeCapabilities.Progress"/>).</param>
    public static string SerializeRequest(int id, AutomationRequest request, bool progress = false)
    {
        var o = new JsonObject
        {
            ["protocol"] = Version,
            ["id"] = id,
            ["command"] = request.Command,
            ["args"] = request.Args.DeepClone(),
        };
        if (progress)
        {
            o["progress"] = true;
        }

        return o.ToJsonString(AutomationJson.Options);
    }

    /// <summary>Serializes an interim progress line for request <paramref name="id"/>.</summary>
    public static string SerializeProgress(JsonNode? id, AutomationProgress progress)
    {
        var o = new JsonObject
        {
            ["protocol"] = Version,
            ["id"] = id?.DeepClone(),
            ["progress"] = new JsonObject
            {
                ["stage"] = progress.Stage,
                ["current"] = progress.Current,
                ["total"] = progress.Total,
            },
        };
        return o.ToJsonString(AutomationJson.Options);
    }

    /// <summary>Serializes the line that cancels the running request <paramref name="id"/>.</summary>
    public static string SerializeCancel(int id) =>
        new JsonObject { ["protocol"] = Version, ["cancel"] = id }.ToJsonString(AutomationJson.Options);

    /// <summary>
    /// True when <paramref name="o"/> is an interim progress line (a <c>progress</c> object and no <c>ok</c>);
    /// <paramref name="progress"/> is then its value.
    /// </summary>
    public static bool TryParseProgress(JsonObject o, out AutomationProgress progress)
    {
        progress = default;
        if (o.ContainsKey("ok") || o["progress"] is not JsonObject p)
        {
            return false;
        }

        try
        {
            progress = new AutomationProgress(
                p["stage"]?.GetValue<string>() ?? string.Empty,
                p["current"]?.GetValue<int>() ?? 0,
                p["total"]?.GetValue<int>() ?? 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            progress = new AutomationProgress(string.Empty, 0, 0);
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="line"/> is a cancel line (an object with <c>cancel</c> and no <c>command</c>);
    /// <paramref name="id"/> is the request id it names.
    /// </summary>
    public static bool TryParseCancel(string line, out JsonNode? id)
    {
        id = null;
        try
        {
            if (JsonNode.Parse(line) is JsonObject o && o.ContainsKey("cancel") && !o.ContainsKey("command"))
            {
                id = o["cancel"]?.DeepClone();
                return true;
            }
        }
        catch (JsonException)
        {
            // not a cancel line
        }

        return false;
    }

    /// <summary>True when two request ids are the same JSON value.</summary>
    public static bool SameId(JsonNode? a, JsonNode? b) =>
        (a is null && b is null) || (a is not null && b is not null && a.ToJsonString() == b.ToJsonString());

    /// <summary>Serializes a response line (without the trailing newline).</summary>
    public static string SerializeResponse(JsonNode? id, AutomationResponse response)
    {
        var o = new JsonObject
        {
            ["protocol"] = Version,
            ["id"] = id?.DeepClone(),
            ["ok"] = response.Ok,
            ["command"] = response.Command,
        };
        if (response.Result is not null)
        {
            o["result"] = response.Result.DeepClone();
        }

        if (response.Error is { } e)
        {
            o["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
        }

        return o.ToJsonString(AutomationJson.Options);
    }

    /// <summary>Parses a response line into an <see cref="AutomationResponse"/>.</summary>
    public static AutomationResponse ParseResponse(string line)
    {
        JsonObject o = JsonNode.Parse(line) as JsonObject
            ?? throw new AutomationException(AutomationErrorCodes.Protocol, "The editor sent a malformed response.");
        AutomationError? error = null;
        if (o["error"] is JsonObject e)
        {
            error = new AutomationError(
                e["code"]?.GetValue<string>() ?? AutomationErrorCodes.Internal,
                e["message"]?.GetValue<string>() ?? string.Empty);
        }

        return new AutomationResponse
        {
            Ok = o["ok"]?.GetValue<bool>() ?? false,
            Command = o["command"]?.GetValue<string>() ?? string.Empty,
            Result = o["result"]?.DeepClone(),
            Error = error,
        };
    }

    /// <summary>Writes one message line.</summary>
    public static async Task WriteLineAsync(Stream stream, string json, CancellationToken cancellation)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }
}

/// <summary>
/// The optional bridge features (<c>hello</c> → <c>capabilities</c>). A client uses a feature only when the
/// editor advertised it, so a newer client works with an older editor and an older client with a newer one.
/// </summary>
public static class BridgeCapabilities
{
    /// <summary>Interim <c>progress</c> lines for requests that ask for them.</summary>
    public const string Progress = "progress";

    /// <summary>A <c>cancel</c> line stops the running request.</summary>
    public const string Cancel = "cancel";

    /// <summary>What this build's bridge server advertises.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Progress, Cancel };
}

/// <summary>The outcome of reading one frame.</summary>
public enum FrameStatus
{
    /// <summary>A complete line was read.</summary>
    Line,

    /// <summary>The line exceeded the size limit; it was discarded up to its newline.</summary>
    TooLarge,

    /// <summary>The peer closed the connection.</summary>
    EndOfStream,
}

/// <summary>Reads newline-delimited frames from a stream with a size guard.</summary>
public sealed class FrameReader
{
    private readonly Stream _stream;
    private readonly int _max;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    public FrameReader(Stream stream, int maxBytes = BridgeProtocol.MaxMessageBytes)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _max = maxBytes;
    }

    /// <summary>Reads the next line (UTF-8, without the newline; a trailing CR is trimmed).</summary>
    public async Task<(FrameStatus Status, string? Line)> ReadAsync(CancellationToken cancellation)
    {
        var line = new MemoryStream();
        bool overflow = false;
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer, cancellation).ConfigureAwait(false);
                if (_end == 0)
                {
                    // EOF: a partial unterminated line is discarded with the connection.
                    return (FrameStatus.EndOfStream, null);
                }
            }

            int nl = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            int take = (nl < 0 ? _end : nl) - _start;
            if (!overflow)
            {
                if (line.Length + take > _max)
                {
                    overflow = true;
                    line.SetLength(0);
                }
                else
                {
                    line.Write(_buffer, _start, take);
                }
            }

            if (nl < 0)
            {
                _start = _end;
                continue;
            }

            _start = nl + 1;
            if (overflow)
            {
                return (FrameStatus.TooLarge, null);
            }

            string text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            return (FrameStatus.Line, text.EndsWith('\r') ? text[..^1] : text);
        }
    }
}

/// <summary>A parsed request line (or why it could not be parsed).</summary>
internal readonly record struct ParsedRequest(JsonNode? Id, AutomationRequest? Request, AutomationError? Error, bool WantsProgress = false)
{
    public static ParsedRequest Parse(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            return new ParsedRequest(null, null, new AutomationError(AutomationErrorCodes.Protocol, $"Malformed JSON: {ex.Message}"));
        }

        if (node is not JsonObject o)
        {
            return new ParsedRequest(null, null, new AutomationError(AutomationErrorCodes.Protocol, "A request must be a JSON object."));
        }

        JsonNode? id = o["id"];
        try
        {
            if (o["protocol"] is JsonNode p && p.GetValue<int>() != BridgeProtocol.Version)
            {
                return new ParsedRequest(id, null, new AutomationError(AutomationErrorCodes.Protocol,
                    $"Unsupported protocol version {p.ToJsonString()} (this editor speaks {BridgeProtocol.Version}). Update Glacier and glacier-cli to the same version."));
            }

            string? command = o["command"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(command))
            {
                return new ParsedRequest(id, null, new AutomationError(AutomationErrorCodes.Protocol, "A request needs a 'command'."));
            }

            JsonObject args = o["args"] switch
            {
                null => new JsonObject(),
                JsonObject a => (JsonObject)a.DeepClone(),
                _ => throw new FormatException("'args' must be an object."),
            };
            bool wantsProgress = o["progress"] is JsonValue pv && pv.TryGetValue(out bool want) && want;
            return new ParsedRequest(id, new AutomationRequest(command, args), null, wantsProgress);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return new ParsedRequest(id, null, new AutomationError(AutomationErrorCodes.Protocol, $"Malformed request: {ex.Message}"));
        }
    }
}

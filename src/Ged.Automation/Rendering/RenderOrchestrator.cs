using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Editor;
using Ged.Rendering.Graphics;

namespace Ged.Automation.Rendering;

/// <summary>
/// Renders a PRIVATE COPY of a session's open level: the level is fetched with the <c>snapshot</c> command
/// (chunked, checksummed), opened in a fresh <see cref="HeadlessAutomationHost"/> with the source's
/// compiled-geometry state, and drawn there by <see cref="RenderCommand"/>. Whatever the render builds (a
/// preview for stale geometry, a sealed hole check) happens on the copy, so a render never changes the
/// source document, its undo history, dirty flag or geometry state. <c>glacier-cli render --attach</c>
/// copies the editor's level this way; the MCP server copies its own headless session's level the same way.
/// </summary>
public static class RenderOrchestrator
{
    /// <summary>
    /// Copies the open level out through <paramref name="send"/> (which executes a <c>snapshot</c> request
    /// against the source session), then renders <paramref name="renderArgs"/> on the copy.
    /// </summary>
    /// <param name="send">Executes one request against the source session (the editor bridge, or an in-process command set).</param>
    /// <param name="renderArgs">The render arguments (validated by <see cref="RenderSpec.FromArgs"/>; <c>level</c> is ignored).</param>
    /// <param name="snapshotArgs">Extra arguments for the first snapshot request (<c>level</c>, <c>origin</c>); null = none.</param>
    /// <param name="createHost">Creates the private session, given the source's RF install (null when it has none).</param>
    /// <param name="deviceFactory">The GPU device (null = <see cref="RenderDevices.Create"/>).</param>
    /// <param name="source">Stamped on the result as <c>source</c> ("editor", "session"); null = not stamped.</param>
    /// <returns>The render response. Throws <see cref="IOException"/> when the source connection fails,
    /// <see cref="OperationCanceledException"/> when canceled, <see cref="AutomationException"/> (<c>protocol</c>) for a corrupt copy.</returns>
    public static async Task<AutomationResponse> RenderCopyAsync(
        Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> send,
        JsonObject renderArgs,
        JsonObject? snapshotArgs,
        Func<string?, HeadlessAutomationHost> createHost,
        Func<(GraphicsDevice Device, string Name)>? deviceFactory,
        string? source,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(renderArgs);
        ArgumentNullException.ThrowIfNull(createHost);

        AutomationResponse first;
        var data = new MemoryStream();
        try
        {
            first = await send(new AutomationRequest("snapshot", (JsonObject?)snapshotArgs?.DeepClone() ?? new JsonObject()), cancellation).ConfigureAwait(false);
            AutomationResponse chunk = first;
            while (true)
            {
                if (!chunk.Ok || chunk.Result is null)
                {
                    return new AutomationResponse
                    {
                        Ok = false,
                        Command = RenderCommand.Name,
                        Error = chunk.Error ?? new AutomationError(AutomationErrorCodes.Protocol, "The session sent no level."),
                    };
                }

                byte[] part = Convert.FromBase64String(chunk.Result["data"]?.GetValue<string>() ?? string.Empty);
                data.Write(part);
                if (chunk.Result["complete"]?.GetValue<bool>() == true)
                {
                    break;
                }

                if (part.Length == 0)
                {
                    throw new AutomationException(AutomationErrorCodes.Protocol, "The level snapshot stopped early.");
                }

                chunk = await send(new AutomationRequest("snapshot", new JsonObject
                {
                    ["token"] = chunk.Result["token"]?.DeepClone(),
                    ["offset"] = (double)data.Length,
                }), cancellation).ConfigureAwait(false);
            }
        }
        catch (FormatException ex)
        {
            throw new AutomationException(AutomationErrorCodes.Protocol, $"The level snapshot was malformed: {ex.Message}");
        }

        JsonNode head = first.Result!;
        byte[] bytes = data.ToArray();
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.Length != head["size"]?.GetValue<int>() || sha != head["sha256"]?.GetValue<string>())
        {
            throw new AutomationException(AutomationErrorCodes.Protocol, "The level snapshot arrived corrupted (size or checksum mismatch).");
        }

        string? sourceRf = head["rfInstallDir"]?.GetValue<string>();
        using HeadlessAutomationHost host = createHost(sourceRf);
        var state = new LevelBuildState(
            head["geometryDirty"]?.GetValue<bool>() == true,
            head["geometryIsPreview"]?.GetValue<bool>() == true,
            head["lightingDirty"]?.GetValue<bool>() == true);
        host.OpenSnapshot(bytes, head["path"]?.GetValue<string>(), state);

        var args = (JsonObject)renderArgs.DeepClone();
        args.Remove("level"); // the copy is already open
        AutomationResponse response = await RenderCommand.ExecuteAsync(host, new AutomationRequest(RenderCommand.Name, args), deviceFactory, cancellation).ConfigureAwait(false);
        if (response.Ok && response.Result is JsonObject result && source is not null)
        {
            result["source"] = source;
            result["level"] = head["path"]?.DeepClone() ?? head["name"]?.DeepClone();
        }

        return response;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation;
using Ged.Automation.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ged.Cli.Mcp;

/// <summary>Converts catalog tools to protocol tools (name, title, description, input schema, annotations).</summary>
internal static class ToolAdapter
{
    public static Tool ToProtocol(ToolSpec spec)
    {
        (bool readOnly, bool destructive, bool idempotent, bool openWorld) = ToolSchema.Annotations(spec);
        return new Tool
        {
            Name = spec.Name,
            Title = ToolSchema.Title(spec),
            Description = spec.Description,
            InputSchema = JsonSerializer.SerializeToElement(ToolSchema.For(spec)),
            Annotations = new ToolAnnotations
            {
                Title = ToolSchema.Title(spec),
                ReadOnlyHint = readOnly,
                DestructiveHint = destructive,
                IdempotentHint = idempotent,
                OpenWorldHint = openWorld,
            },
        };
    }
}

/// <summary>
/// The SDK front end of <c>glacier-cli mcp</c> (the only code that touches protocol types): builds the server
/// from a <see cref="McpToolService"/> — tool listing, tool calls (structured content plus a compact JSON text
/// block, and an image block for renders), progress notifications, resources (<see cref="McpResources"/>) and
/// prompts (<see cref="McpPrompts"/>) — and runs it over a transport (standard input/output, or in-memory streams
/// in tests).
/// </summary>
internal sealed class McpServerHost
{
    private readonly McpToolService _service;

    public McpServerHost(McpToolService service) => _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>The server options: identity, instructions, and the tool handlers.</summary>
    public McpServerOptions CreateOptions() => new()
    {
        ServerInfo = new Implementation { Name = "glacier", Title = "Glacier", Version = AutomationCommands.ProductVersion },
        ServerInstructions = _service.Instructions,
        Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability { ListChanged = false },
            Resources = new ResourcesCapability { Subscribe = false, ListChanged = false },
            Prompts = new PromptsCapability { ListChanged = false },
        },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (request, ct) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = _service.Tools.Select(ToolAdapter.ToProtocol).ToList(),
            }),
            CallToolHandler = CallToolAsync,
            ListResourcesHandler = (request, ct) => ValueTask.FromResult(new ListResourcesResult
            {
                Resources = McpResources.List(_service.OffersLevelSummary).Select(r => new Resource
                {
                    Uri = r.Uri,
                    Name = r.Name,
                    Title = r.Title,
                    Description = r.Description,
                    MimeType = r.MimeType,
                }).ToList(),
            }),
            ListResourceTemplatesHandler = (request, ct) => ValueTask.FromResult(new ListResourceTemplatesResult
            {
                ResourceTemplates = McpResources.Templates.Select(t => new ResourceTemplate
                {
                    UriTemplate = t.UriTemplate,
                    Name = t.Name,
                    Title = t.Title,
                    Description = t.Description,
                    MimeType = t.MimeType,
                }).ToList(),
            }),
            ReadResourceHandler = ReadResourceAsync,
            ListPromptsHandler = (request, ct) => ValueTask.FromResult(new ListPromptsResult
            {
                Prompts = McpPrompts.All.Select(p => new Prompt
                {
                    Name = p.Name,
                    Title = p.Title,
                    Description = p.Description,
                    Arguments = p.Args.Select(a => new PromptArgument { Name = a.Name, Description = a.Description, Required = a.Required }).ToList(),
                }).ToList(),
            }),
            GetPromptHandler = GetPromptAsync,
        },
    };

    private async ValueTask<ReadResourceResult> ReadResourceAsync(RequestContext<ReadResourceRequestParams> request, CancellationToken cancellation)
    {
        string uri = request.Params?.Uri ?? throw new McpProtocolException("Missing resource URI.", McpErrorCode.InvalidParams);
        McpResourceText? text;
        try
        {
            text = await _service.ReadResourceAsync(uri, cancellation).ConfigureAwait(false);
        }
        catch (AutomationException ex)
        {
            throw new McpProtocolException(ex.Message,
                ex.Code == AutomationErrorCodes.Usage ? McpErrorCode.ResourceNotFound : McpErrorCode.InternalError);
        }

        if (text is null)
        {
            throw new McpProtocolException(
                $"Unknown resource '{uri}'. Resources: {string.Join(", ", McpResources.List(_service.OffersLevelSummary).Select(r => r.Uri))}, {string.Join(", ", McpResources.Templates.Select(t => t.UriTemplate))}.",
                McpErrorCode.ResourceNotFound);
        }

        return new ReadResourceResult
        {
            Contents = new List<ResourceContents> { new TextResourceContents { Uri = text.Uri, MimeType = text.MimeType, Text = text.Text } },
        };
    }

    private ValueTask<GetPromptResult> GetPromptAsync(RequestContext<GetPromptRequestParams> request, CancellationToken cancellation)
    {
        GetPromptRequestParams p = request.Params ?? throw new McpProtocolException("Missing prompt parameters.", McpErrorCode.InvalidParams);
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (p.Arguments is { } given)
        {
            foreach (KeyValuePair<string, JsonElement> kv in given)
            {
                arguments[kv.Key] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? string.Empty : kv.Value.GetRawText();
            }
        }

        string text;
        try
        {
            text = McpPrompts.Render(p.Name, arguments);
        }
        catch (AutomationException ex)
        {
            throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
        }

        return ValueTask.FromResult(new GetPromptResult
        {
            Description = McpPrompts.Find(p.Name)?.Description,
            Messages = new List<PromptMessage> { new() { Role = Role.User, Content = new TextContentBlock { Text = text } } },
        });
    }

    /// <summary>Serves on standard input/output until the client disconnects or <paramref name="cancellation"/> fires.</summary>
    public async Task RunStdioAsync(CancellationToken cancellation)
    {
        McpServerOptions options = CreateOptions();
        await using var transport = new StdioServerTransport(options, loggerFactory: null);
        await using McpServer server = McpServer.Create(transport, options);
        await server.RunAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>Serves on a pair of streams (tests: an in-memory duplex).</summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellation)
    {
        McpServerOptions options = CreateOptions();
        await using var transport = new StreamServerTransport(input, output, "glacier");
        await using McpServer server = McpServer.Create(transport, options);
        await server.RunAsync(cancellation).ConfigureAwait(false);
    }

    private async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellation)
    {
        CallToolRequestParams p = request.Params ?? throw new McpProtocolException("Missing tool call parameters.", McpErrorCode.InvalidParams);
        JsonObject? args = null;
        if (p.Arguments is { Count: > 0 } given)
        {
            args = new JsonObject();
            foreach (KeyValuePair<string, JsonElement> kv in given)
            {
                args[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
            }
        }

        // Progress: a strictly increasing count (stages restart their own n/m), the stage text as the message.
        // Notifications are written before the result: the call waits for every one it started.
        Action<AutomationProgress>? progress = null;
        var pending = new List<Task>();
        if (p.ProgressToken is { } token)
        {
            long sequence = 0;
            McpServer server = request.Server;
            progress = value =>
            {
                long n = Interlocked.Increment(ref sequence);
                string message = value.Total > 1 ? $"{value.Stage} ({value.Current}/{value.Total})" : value.Stage;
                var value2 = new ProgressNotificationValue { Progress = n, Message = message };
                lock (pending)
                {
                    // Chained so notifications go out in order.
                    Task previous = pending.Count > 0 ? pending[^1] : Task.CompletedTask;
                    pending.Add(previous.ContinueWith(
                        _ => server.NotifyProgressAsync(token, value2, cancellationToken: CancellationToken.None),
                        CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap());
                }
            };
        }

        ToolOutcome outcome = await _service.CallAsync(p.Name, args, progress, cancellation).ConfigureAwait(false);
        Task[] sends;
        lock (pending)
        {
            sends = pending.ToArray();
        }

        try
        {
            await Task.WhenAll(sends).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // A progress notification that could not be written does not fail the call.
        }
        string text = outcome.Structured.ToJsonString(AutomationJson.Options);
        var content = new List<ContentBlock> { new TextContentBlock { Text = text } };
        if (outcome.Png is { } png)
        {
            content.Add(ImageContentBlock.FromBytes(png, "image/png"));
        }

        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(outcome.Structured),
            IsError = outcome.IsError,
        };
    }
}

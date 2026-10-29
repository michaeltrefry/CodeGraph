using System.Text.Json;
using CodeGraph.Models.Work;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeGraph.Services.Work;

/// <summary>
/// One work tool on the MCP server. Its schema is generated from the tool's argument type with
/// <see cref="WorkJson.Options"/> (every object <c>additionalProperties: false</c>), and a call
/// hands the raw arguments to <see cref="IWorkService"/>, the same path REST takes, so nothing the
/// SDK's own binding would drop silently ever reaches a write (E1). Results are the object as JSON
/// (text and structured content); a refusal is <c>isError</c> with <c>{code, message}</c>.
/// </summary>
public sealed class WorkMcpTool : McpServerTool
{
    private readonly WorkToolDefinition definition;

    public WorkMcpTool(WorkToolDefinition definition)
    {
        this.definition = definition;
        ProtocolTool = new Tool
        {
            Name = definition.Name,
            Title = definition.Title,
            Description = definition.Description,
            InputSchema = WorkJson.SchemaOf(definition.ArgumentsType),
            Annotations = new ToolAnnotations
            {
                Title = definition.Title,
                ReadOnlyHint = definition.ReadOnly,
                DestructiveHint = false,
                IdempotentHint = definition.Idempotent,
                OpenWorldHint = false
            }
        };
    }

    public static IEnumerable<McpServerTool> CreateAll() => WorkTools.All.Select(definition => new WorkMcpTool(definition));

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata { get; } = [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var services = request.Services ?? throw new InvalidOperationException("The MCP request carries no service provider.");
        var service = services.GetRequiredService<IWorkService>();
        var arguments = JsonSerializer.SerializeToElement(
            request.Params?.Arguments ?? new Dictionary<string, JsonElement>(), WorkJson.Options);
        try
        {
            var result = await service.ExecuteAsync(definition.Name, arguments, WorkService.ActorFor(request.User), cancellationToken);
            var element = JsonSerializer.SerializeToElement(result, result.GetType(), WorkJson.Options);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = element.GetRawText() }],
                StructuredContent = element
            };
        }
        catch (WorkException exception)
        {
            return ToError(exception);
        }
        catch (WorkStoreUnavailableException exception)
        {
            // Not in the error vocabulary: a plain tool error with no structured code.
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = exception.Message }] };
        }
    }

    public static CallToolResult ToError(WorkException exception)
    {
        var element = JsonSerializer.SerializeToElement(new WorkError(exception.Code, exception.Message, exception.Current, exception.Reason), WorkJson.Options);
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element
        };
    }
}

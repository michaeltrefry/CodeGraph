using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Query;

namespace CodeGraph.Services.Assistant;

/// <summary>
/// Streaming agentic assistant that answers natural-language questions about the
/// codebase by running an agent loop with read-only graph query tools.
/// </summary>
public partial class GraphAssistant(
    AnthropicClient client,
    GraphQueryEngine query,
    IGraphStore store,
    IWikiStore wikiStore,
    RepositorySourceOptions sourceOptions,
    AnalysisOptions options,
    ILogger<GraphAssistant> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Answer a natural-language question about the codebase, yielding SSE events
    /// as they arrive.  Event types: "text", "tool_use", "done", "error".
    /// </summary>
    public async IAsyncEnumerable<AssistantEvent> AskAsync(
        string question,
        string? context = null,
        IReadOnlyList<ChatMessage>? history = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var userMessage = string.IsNullOrWhiteSpace(context)
            ? question
            : $"[Context: {context}]\n\n{question}";

        var messages = new List<MessageParam>();

        // Replay prior conversation turns (user/assistant text only — tool calls are not replayed)
        if (history is { Count: > 0 })
        {
            foreach (var msg in history)
            {
                var role = msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                    ? Role.Assistant
                    : Role.User;
                messages.Add(new MessageParam { Role = role, Content = msg.Content });
            }
        }

        messages.Add(new MessageParam { Role = Role.User, Content = userMessage });

        var tools = BuildTools();
        bool exhaustedTurns = false;

        for (int turn = 0; turn < options.AssistantMaxTurns; turn++)
        {
            logger.LogInformation("Agent turn {Turn}/{MaxTurns} — sending {MessageCount} messages to Claude",
                turn + 1, options.AssistantMaxTurns, messages.Count);
            var turnSw = System.Diagnostics.Stopwatch.StartNew();

            var textSb = new StringBuilder();
            var toolUseList = new List<PendingToolUse>();
            PendingToolUse? current = null;

            int streamEventCount = 0;
            await foreach (var e in client.Messages.CreateStreaming(new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = options.AssistantMaxTokens,
                System = GetSystemPrompt(),
                Messages = messages,
                Tools = tools
            }, ct))
            {
                streamEventCount++;
                if (e.TryPickContentBlockStart(out var cbStart))
                {
                    if (cbStart.ContentBlock.TryPickToolUse(out var tu))
                    {
                        current = new PendingToolUse(tu.ID, tu.Name, new StringBuilder());
                        yield return new AssistantEvent("tool_use", tu.Name);
                    }
                    else
                    {
                        logger.LogDebug("Turn {Turn} content block start (non-tool): index {Index}", turn + 1, cbStart.Index);
                    }
                }
                else if (e.TryPickContentBlockDelta(out var cbDelta))
                {
                    if (cbDelta.Delta.TryPickText(out var td))
                    {
                        textSb.Append(td.Text);
                        yield return new AssistantEvent("text", td.Text);
                    }
                    else if (cbDelta.Delta.TryPickInputJSON(out var ij) && current is not null)
                    {
                        current.Input.Append(ij.PartialJSON);
                    }
                }
                else if (e.TryPickContentBlockStop(out _) && current is not null)
                {
                    toolUseList.Add(current);
                    current = null;
                }
            }
            logger.LogInformation("Turn {Turn} raw stream: {StreamEvents} SDK events, {TextLen} text chars, {ToolCount} tool blocks",
                turn + 1, streamEventCount, textSb.Length, toolUseList.Count);

            // No tool calls → final answer already streamed
            if (toolUseList.Count == 0)
            {
                logger.LogInformation("Turn {Turn} — no tool calls, breaking (final answer: {TextLen} chars)", turn + 1, textSb.Length);
                exhaustedTurns = false;
                break;
            }
            logger.LogInformation("Turn {Turn} — {ToolCount} tool calls, continuing loop", turn + 1, toolUseList.Count);

            // Build assistant turn message
            var assistantContent = new List<ContentBlockParam>();
            if (textSb.Length > 0)
                assistantContent.Add(new ContentBlockParam(new TextBlockParam { Text = textSb.ToString() }));

            foreach (var tu in toolUseList)
            {
                var inputJson = tu.Input.Length > 0 ? tu.Input.ToString() : "{}";
                var inputDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputJson) ?? new();
                assistantContent.Add(new ContentBlockParam(
                    new ToolUseBlockParam { ID = tu.Id, Name = tu.Name, Input = inputDict }));
            }

            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });

            // Execute tools and build results turn
            var resultBlocks = new List<ContentBlockParam>();
            foreach (var tu in toolUseList)
            {
                logger.LogInformation("Executing tool {Tool}", tu.Name);
                var toolSw = System.Diagnostics.Stopwatch.StartNew();
                string result;
                try
                {
                    var inputJson = tu.Input.Length > 0 ? tu.Input.ToString() : "{}";
                    var input = JsonSerializer.Deserialize<JsonElement>(inputJson);
                    result = await ExecuteToolAsync(tu.Name, input, ct);
                    logger.LogInformation("Tool {Tool} completed in {Elapsed}ms — {ResultLen} chars", tu.Name, toolSw.ElapsedMilliseconds, result.Length);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or ArgumentException)
                {
                    logger.LogWarning(ex, "Tool {Tool} failed after {Elapsed}ms", tu.Name, toolSw.ElapsedMilliseconds);
                    result = $"Tool error: {ex.Message}";
                }

                resultBlocks.Add(new ContentBlockParam(
                    new ToolResultBlockParam(tu.Id)
                    {
                        Content = new ToolResultBlockParamContent(result)
                    }));
            }

            messages.Add(new MessageParam { Role = Role.User, Content = resultBlocks });
            exhaustedTurns = true;
        }

        if (exhaustedTurns)
        {
            logger.LogInformation("Max turns exhausted — forcing synthesis with {MessageCount} messages", messages.Count);
            messages.Add(new MessageParam
            {
                Role = Role.User,
                Content = "You've gathered enough information. Please synthesize everything you've found and provide a comprehensive answer to the original question. Do not call any more tools."
            });

            await foreach (var e in client.Messages.CreateStreaming(new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = options.AssistantMaxTokens,
                System = GetSystemPrompt(),
                Messages = messages
            }, ct))
            {
                if (e.TryPickContentBlockDelta(out var cbDelta) && cbDelta.Delta.TryPickText(out var td))
                {
                    yield return new AssistantEvent("text", td.Text);
                }
            }
        }

        yield return new AssistantEvent("done", "");
    }

    // ── Tool dispatch ────────────────────────────────────────────────────

    private async Task<string> ExecuteToolAsync(string name, JsonElement input, CancellationToken ct)
    {
        return name switch
        {
            "search_graph" => await SearchGraphAsync(input),
            "list_projects" => await ListProjectsAsync(),
            "get_service_summary" => await GetServiceSummaryAsync(input),
            "trace_call_path" => await TraceCallPathAsync(input),
            "trace_data_lineage" => await TraceDataLineageAsync(input),
            "find_consumers" => await FindConsumersAsync(input),
            "find_publishers" => await FindPublishersAsync(input),
            "get_architecture" => await GetArchitectureAsync(input),
            "find_archival_candidates" => await FindArchivalCandidatesAsync(),
            "get_project_health" => await GetProjectHealthAsync(input),
            "get_fleet_health" => await GetFleetHealthAsync(input),
            "read_node_source" => await ReadNodeSourceAsync(input),
            "get_code_snippet" => await GetCodeSnippetAsync(input),
            "get_graph_schema" => GetGraphSchema(),
            "list_conventions" => await ListConventionsAsync(),
            "get_convention" => await GetConventionAsync(input),
            _ => $"Unknown tool: {name}"
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string? GetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? GetInt(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static string GetSystemPrompt() => """
        You are an expert assistant for a codebase knowledge graph covering 620+ repositories
        at a domain name reseller and auctioneer (HugeDomains.com, DropCatch.com, NameBright.com).

        You have access to tools that query the structural graph of all repositories.
        Use them to answer questions about service dependencies, API endpoints, events,
        data flow, architecture, and repository health. Always search before answering.
        For health questions, use get_project_health (single repo) or get_fleet_health (all repos).
        When you need to see actual code, use read_node_source (by node ID) or get_code_snippet (by file path).

        Business context:
        - Drop catching: competing to register expiring domains at the moment they're deleted
        - Domain valuation: AI-augmented scoring before purchase decisions
        - EPP: Extensible Provisioning Protocol for registry communication
        - TC.*.Models NuGet packages are the canonical cross-repo linking keys

        When answering, cite specific repositories, node names, and edge types.
        Be specific and concrete — use the data from the graph, not guesses.
        """;

    private sealed record PendingToolUse(string Id, string Name, StringBuilder Input);
}

public record AssistantEvent(string Type, string Content);

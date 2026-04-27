using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Requests;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Prompts;
using CodeGraph.Services.Query;
using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;

namespace CodeGraph.Services.Assistant;

/// <summary>
/// Streaming agentic assistant that answers natural-language questions about the
/// codebase by running an agent loop with read-only graph query tools.
/// </summary>
public partial class GraphAssistant(
    AnthropicClient anthropicClient,
    IHttpClientFactory httpClientFactory,
    GraphQueryEngine query,
    IGraphStore store,
    IWikiStore wikiStore,
    IOptions<RepositorySourceOptions> sourceOptionsAccessor,
    IOptions<AnalysisOptions> optionsAccessor,
    IMetricsEventPublisher metricsEventPublisher,
    IAssistantDebugCapture debugCapture,
    ILogger<GraphAssistant> logger,
    IAgentPromptService? agentPromptService = null)
{
    private readonly RepositorySourceOptions sourceOptions = sourceOptionsAccessor.Value;
    private readonly AnalysisOptions options = optionsAccessor.Value;
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
        string? username = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var provider = ResolveAssistantProvider();

        switch (provider)
        {
            case "anthropic":
                await foreach (var e in AskWithAnthropicAsync(question, context, history, username, ct))
                    yield return e;
                yield break;

            case "openai":
            case "local":
                await foreach (var e in AskWithOpenAiCompatibleAsync(provider, question, context, history, username, ct))
                    yield return e;
                yield break;

            default:
                throw new InvalidOperationException(
                    $"Assistant provider '{provider}' is not supported. Supported providers: anthropic, openai, local");
        }
    }

    private async IAsyncEnumerable<AssistantEvent> AskWithAnthropicAsync(
        string question,
        string? context,
        IReadOnlyList<ChatMessage>? history,
        string? username,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var client = CreateConfiguredAnthropicClient();
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

        var tools = BuildAnthropicTools();
        bool exhaustedTurns = false;
        bool hasUsedTool = false;

        for (int turn = 0; turn < options.Assistant.MaxTurns; turn++)
        {
            logger.LogInformation("Agent turn {Turn}/{MaxTurns} — sending {MessageCount} messages to Claude",
                turn + 1, options.Assistant.MaxTurns, messages.Count);
            var turnSw = System.Diagnostics.Stopwatch.StartNew();
            var allowTextStreaming = hasUsedTool;

            var textSb = new StringBuilder();
            var toolUseList = new List<PendingToolUse>();
            PendingToolUse? current = null;
            MessageDeltaUsage? usage = null;
            var model = ResolveAssistantModel("anthropic");

            int streamEventCount = 0;
            await foreach (var e in client.Messages.CreateStreaming(new MessageCreateParams
            {
                Model = model,
                MaxTokens = options.Assistant.MaxTokens,
                System = await GetSystemPromptAsync(),
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
                        if (allowTextStreaming)
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
                else if (e.TryPickDelta(out var messageDelta))
                {
                    usage = messageDelta.Usage;
                }
            }
            await PublishAnthropicUsageAsync(usage, username, "assistant.ask", model, ct);
            await CaptureAnthropicDebugExchangeAsync(
                turn,
                model,
                messages.Count,
                allowTextStreaming,
                textSb.ToString(),
                toolUseList,
                usage,
                streamEventCount,
                question,
                context,
                history,
                ct);
            logger.LogInformation("Turn {Turn} raw stream: {StreamEvents} SDK events, {TextLen} text chars, {ToolCount} tool blocks",
                turn + 1, streamEventCount, textSb.Length, toolUseList.Count);

            // No tool calls → final answer already streamed
            if (toolUseList.Count == 0)
            {
                if (!hasUsedTool)
                {
                    logger.LogWarning("Turn {Turn} returned no tool calls before grounding; retrying with explicit tool requirement", turn + 1);
                    messages.Add(new MessageParam
                    {
                        Role = Role.User,
                        Content = GetToolRequirementReminder()
                    });
                    exhaustedTurns = false;
                    continue;
                }

                logger.LogInformation("Turn {Turn} — no tool calls, breaking (final answer: {TextLen} chars)", turn + 1, textSb.Length);
                exhaustedTurns = false;
                break;
            }

            if (!allowTextStreaming && textSb.Length > 0)
                yield return new AssistantEvent("text", textSb.ToString());

            hasUsedTool = true;
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
                    result = await ExecuteToolAsync(tu.Name, input, username, ct);
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

        if (!hasUsedTool)
            throw new InvalidOperationException("Assistant did not call any tools. The configured provider may not support tool calling for the Ask workflow.");

        if (exhaustedTurns)
        {
            logger.LogInformation("Max turns exhausted — forcing synthesis with {MessageCount} messages", messages.Count);
            messages.Add(new MessageParam
            {
                Role = Role.User,
                Content = "You've gathered enough information. Please synthesize everything you've found and provide a comprehensive answer to the original question. Do not call any more tools."
            });

            MessageDeltaUsage? usage = null;
            var finalTextSb = new StringBuilder();
            var finalStreamEventCount = 0;
            var model = ResolveAssistantModel("anthropic");
            await foreach (var e in client.Messages.CreateStreaming(new MessageCreateParams
            {
                Model = model,
                MaxTokens = options.Assistant.MaxTokens,
                System = await GetSystemPromptAsync(),
                Messages = messages
            }, ct))
            {
                finalStreamEventCount++;
                if (e.TryPickContentBlockDelta(out var cbDelta) && cbDelta.Delta.TryPickText(out var td))
                {
                    finalTextSb.Append(td.Text);
                    yield return new AssistantEvent("text", td.Text);
                }
                else if (e.TryPickDelta(out var messageDelta))
                {
                    usage = messageDelta.Usage;
                }
            }
            await PublishAnthropicUsageAsync(usage, username, "assistant.ask", model, ct);
            await CaptureAnthropicDebugExchangeAsync(
                options.Assistant.MaxTurns,
                model,
                messages.Count,
                allowTextStreaming: true,
                finalTextSb.ToString(),
                [],
                usage,
                finalStreamEventCount,
                question,
                context,
                history,
                ct);
        }

        yield return new AssistantEvent("done", "");
    }

    private async IAsyncEnumerable<AssistantEvent> AskWithOpenAiCompatibleAsync(
        string provider,
        string question,
        string? context,
        IReadOnlyList<ChatMessage>? history,
        string? username,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var userMessage = string.IsNullOrWhiteSpace(context)
            ? question
            : $"[Context: {context}]\n\n{question}";

        var messages = new List<OpenAiChatMessage>();
        if (history is { Count: > 0 })
        {
            foreach (var msg in history)
            {
                messages.Add(new OpenAiChatMessage
                {
                    Role = msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
                    Content = msg.Content
                });
            }
        }

        messages.Add(new OpenAiChatMessage
        {
            Role = "user",
            Content = userMessage
        });

        var tools = BuildOpenAiTools();
        var http = httpClientFactory.CreateClient();
        var maxTurns = options.Assistant.MaxTurns;
        bool exhaustedTurns = false;
        bool hasUsedTool = false;

        for (int turn = 0; turn < maxTurns; turn++)
        {
            logger.LogInformation("Agent turn {Turn}/{MaxTurns} — sending {MessageCount} messages to {Provider}",
                turn + 1, maxTurns, messages.Count, provider);

            var completion = await CompleteOpenAiCompatibleTurnAsync(
                http,
                provider,
                messages,
                tools,
                username,
                "assistant.ask",
                ct);
            await CaptureOpenAiCompatibleDebugExchangeAsync(
                provider,
                turn,
                messages,
                tools,
                completion,
                ct);
            var choice = completion.Choices.FirstOrDefault()
                ?? throw new InvalidOperationException($"{provider} returned no assistant choices");
            var assistantMessage = choice.Message
                ?? throw new InvalidOperationException($"{provider} returned an empty assistant message");

            if (assistantMessage.ToolCalls is not { Count: > 0 })
            {
                if (!hasUsedTool)
                {
                    logger.LogWarning("{Provider} assistant turn {Turn} returned no tool calls before grounding; retrying with explicit tool requirement",
                        provider, turn + 1);
                    messages.Add(new OpenAiChatMessage
                    {
                        Role = "user",
                        Content = GetToolRequirementReminder()
                    });
                    exhaustedTurns = false;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(assistantMessage.Content))
                    yield return new AssistantEvent("text", assistantMessage.Content);

                exhaustedTurns = false;
                break;
            }

            if (!string.IsNullOrWhiteSpace(assistantMessage.Content))
                yield return new AssistantEvent("text", assistantMessage.Content);

            hasUsedTool = true;
            foreach (var toolCall in assistantMessage.ToolCalls)
                yield return new AssistantEvent("tool_use", toolCall.Function?.Name ?? "unknown_tool");

            messages.Add(new OpenAiChatMessage
            {
                Role = "assistant",
                Content = assistantMessage.Content,
                ToolCalls = assistantMessage.ToolCalls
                    .Select(toolCall => new OpenAiToolCall
                    {
                        Id = toolCall.Id,
                        Type = toolCall.Type,
                        Function = toolCall.Function is null
                            ? null
                            : new OpenAiToolFunction
                            {
                                Name = toolCall.Function.Name,
                                Arguments = toolCall.Function.Arguments
                            }
                    })
                    .ToList()
            });

            foreach (var toolCall in assistantMessage.ToolCalls)
            {
                var toolName = toolCall.Function?.Name ?? "unknown_tool";
                var inputJson = string.IsNullOrWhiteSpace(toolCall.Function?.Arguments)
                    ? "{}"
                    : toolCall.Function!.Arguments!;

                string result;
                try
                {
                    var input = JsonSerializer.Deserialize<JsonElement>(inputJson);
                    result = await ExecuteToolAsync(toolName, input, username, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or ArgumentException)
                {
                    logger.LogWarning(ex, "Tool {Tool} failed while executing assistant turn", toolName);
                    result = $"Tool error: {ex.Message}";
                }

                messages.Add(new OpenAiChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    Content = result
                });
            }

            exhaustedTurns = true;
        }

        if (!hasUsedTool)
            throw new InvalidOperationException("Assistant did not call any tools. The configured provider may not support tool calling for the Ask workflow.");

        if (exhaustedTurns)
        {
            logger.LogInformation("Max turns exhausted for {Provider} assistant — forcing synthesis", provider);

            messages.Add(new OpenAiChatMessage
            {
                Role = "user",
                Content = "You've gathered enough information. Please synthesize everything you've found and provide a comprehensive answer to the original question. Do not call any more tools."
            });

            var completion = await CompleteOpenAiCompatibleTurnAsync(
                http,
                provider,
                messages,
                tools: null,
                username,
                "assistant.ask",
                ct);
            await CaptureOpenAiCompatibleDebugExchangeAsync(
                provider,
                maxTurns,
                messages,
                tools: null,
                completion,
                ct);
            var finalMessage = completion.Choices.FirstOrDefault()?.Message;
            if (!string.IsNullOrWhiteSpace(finalMessage?.Content))
                yield return new AssistantEvent("text", finalMessage.Content);
        }

        yield return new AssistantEvent("done", "");
    }

    // ── Tool dispatch ────────────────────────────────────────────────────

    private async Task<string> ExecuteToolAsync(string name, JsonElement input, string? username, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await ExecuteToolCoreAsync(name, input);
            await PublishToolInvocationTelemetryAsync(name, true, sw.ElapsedMilliseconds, username, null, ct);
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await PublishToolInvocationTelemetryAsync(name, false, sw.ElapsedMilliseconds, username, ex.GetType().Name, CancellationToken.None);
            throw;
        }
    }

    private async Task<string> ExecuteToolCoreAsync(string name, JsonElement input)
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

    private async Task PublishToolInvocationTelemetryAsync(
        string toolName,
        bool success,
        long durationMs,
        string? username,
        string? errorCode,
        CancellationToken ct)
    {
        try
        {
            await metricsEventPublisher.PublishMcpToolInvocationAsync(
                new McpToolInvocationRecord(
                    toolName,
                    success,
                    durationMs,
                    NormalizeTelemetryUsername(username),
                    ErrorCode: errorCode),
                ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to record assistant tool telemetry for {ToolName}", toolName);
        }
    }

    private async Task CaptureAnthropicDebugExchangeAsync(
        int turnIndex,
        string model,
        int messageCount,
        bool allowTextStreaming,
        string responseText,
        IReadOnlyList<PendingToolUse> toolUseList,
        MessageDeltaUsage? usage,
        int streamEventCount,
        string question,
        string? context,
        IReadOnlyList<ChatMessage>? history,
        CancellationToken ct)
    {
        var inputTokens = ClampTokenCount(SaturatingSum(
            PositiveOrZero(usage?.InputTokens),
            PositiveOrZero(usage?.CacheCreationInputTokens),
            PositiveOrZero(usage?.CacheReadInputTokens)));
        var outputTokens = ClampTokenCount(PositiveOrZero(usage?.OutputTokens));
        var totalTokens = ClampTokenCount(SaturatingSum(inputTokens, outputTokens));

        await debugCapture.CaptureExchangeAsync(new AssistantDebugExchangeCapture(
            TurnIndex: turnIndex,
            Provider: "anthropic",
            Model: model,
            RequestBodyJson: SerializeDebugJson(new
            {
                provider = "anthropic",
                model,
                turnIndex,
                messageCount,
                maxTokens = options.Assistant.MaxTokens,
                allowTextStreaming,
                toolNames = BuildToolDefinitions().Select(tool => tool.Name).ToList()
            }) ?? "{}",
            RequestText: BuildInitialAssistantRequestText(question, context, history),
            ResponseBodyJson: SerializeDebugJson(new
            {
                provider = "anthropic",
                model,
                turnIndex,
                streamEventCount,
                textLength = responseText.Length,
                toolUseCount = toolUseList.Count
            }),
            ResponseText: responseText,
            ToolUsesJson: SerializeToolUses(toolUseList),
            ResponseMetadataJson: SerializeDebugJson(new { streamEventCount }),
            InputTokens: totalTokens == 0 ? null : inputTokens,
            OutputTokens: totalTokens == 0 ? null : outputTokens,
            TotalTokens: totalTokens == 0 ? null : totalTokens),
            ct);
    }

    private async Task CaptureOpenAiCompatibleDebugExchangeAsync(
        string provider,
        int turnIndex,
        IReadOnlyList<OpenAiChatMessage> messages,
        IReadOnlyList<OpenAiToolDefinition>? tools,
        OpenAiChatCompletionResponse completion,
        CancellationToken ct)
    {
        var model = FirstNonEmpty(completion.Model, ResolveAssistantModel(provider), "unknown");
        var assistantMessage = completion.Choices.FirstOrDefault()?.Message;

        await debugCapture.CaptureExchangeAsync(new AssistantDebugExchangeCapture(
            TurnIndex: turnIndex,
            Provider: provider,
            Model: model,
            RequestBodyJson: SerializeDebugJson(new
            {
                provider,
                model = ResolveAssistantModel(provider),
                turnIndex,
                messageCount = messages.Count,
                maxTokens = options.Assistant.MaxTokens,
                toolNames = tools?.Select(tool => tool.Function.Name).ToList()
            }) ?? "{}",
            RequestText: BuildOpenAiRequestText(messages),
            ResponseBodyJson: SerializeDebugJson(completion),
            ResponseText: assistantMessage?.Content,
            ToolUsesJson: SerializeDebugJson(assistantMessage?.ToolCalls?.Select(toolCall => new
            {
                toolCall.Id,
                toolCall.Type,
                Name = toolCall.Function?.Name,
                Arguments = toolCall.Function?.Arguments
            }).ToList()),
            InputTokens: completion.Usage?.PromptTokens,
            OutputTokens: completion.Usage?.CompletionTokens,
            TotalTokens: completion.Usage?.TotalTokens),
            ct);
    }

    private static string BuildInitialAssistantRequestText(
        string question,
        string? context,
        IReadOnlyList<ChatMessage>? history)
    {
        var sb = new StringBuilder();
        if (history is { Count: > 0 })
        {
            foreach (var message in history)
                sb.Append(message.Role).Append(": ").AppendLine(message.Content);
        }

        if (!string.IsNullOrWhiteSpace(context))
            sb.Append("[Context: ").Append(context).AppendLine("]");

        sb.Append("user: ").Append(question);
        return sb.ToString();
    }

    private static string BuildOpenAiRequestText(IReadOnlyList<OpenAiChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            sb.Append(message.Role).Append(": ");
            if (!string.IsNullOrWhiteSpace(message.Content))
                sb.Append(message.Content);
            if (message.ToolCalls is { Count: > 0 })
                sb.Append(" [tool calls: ").Append(string.Join(", ", message.ToolCalls.Select(t => t.Function?.Name ?? "unknown"))).Append(']');
            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
                sb.Append(" [tool result: ").Append(message.ToolCallId).Append(']');
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? SerializeToolUses(IReadOnlyList<PendingToolUse> toolUseList)
    {
        if (toolUseList.Count == 0)
            return null;

        return SerializeDebugJson(toolUseList.Select(toolUse => new
        {
            toolUse.Id,
            toolUse.Name,
            InputJson = toolUse.Input.Length == 0 ? "{}" : toolUse.Input.ToString()
        }).ToList());
    }

    private static string? SerializeDebugJson<T>(T? value)
    {
        if (value is null)
            return null;

        return JsonSerializer.Serialize(value, CodeGraphJsonDefaults.CamelCase);
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

    private Task<string> GetSystemPromptAsync()
        => AgentPromptExecution.GetEffectivePromptOrDefaultAsync(
            agentPromptService,
            AgentPromptCatalog.GraphAssistantSystemPromptKey,
            AgentPromptCatalog.GraphAssistantDefaultSystemPrompt,
            logger,
            "graph assistant");

    private static string GetToolRequirementReminder() =>
        "Before answering, you must call at least one tool to ground your response in repository data. Do not answer from general knowledge alone.";

    private IAnthropicClient CreateConfiguredAnthropicClient()
    {
        var client = anthropicClient;
        var apiKey = FirstNonEmpty(options.Assistant.Anthropic.ApiKey, options.Anthropic.ApiKey);
        var baseUrl = FirstNonEmpty(options.Assistant.Anthropic.BaseUrl, TryGetAnthropicBaseUrl(options.Anthropic.MessagesApiUrl));

        if (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(baseUrl))
            return client;

        return client.WithOptions(config => config with
        {
            APIKey = string.IsNullOrWhiteSpace(apiKey) ? config.APIKey : apiKey,
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? config.BaseUrl : new Uri(baseUrl)
        });
    }

    private async Task<OpenAiChatCompletionResponse> CompleteOpenAiCompatibleTurnAsync(
        HttpClient http,
        string provider,
        IReadOnlyList<OpenAiChatMessage> messages,
        IReadOnlyList<OpenAiToolDefinition>? tools,
        string? username,
        string path,
        CancellationToken ct)
    {
        var request = await CreateOpenAiCompatibleRequestAsync(provider, messages, tools);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Status Code: {response.StatusCode} {errorBody}");
        }

        var completion = await response.Content.ReadFromJsonAsync<OpenAiChatCompletionResponse>(CodeGraphJsonDefaults.SnakeCase, ct)
            ?? throw new InvalidOperationException($"{provider} returned a null assistant response");

        await PublishOpenAiCompatibleUsageAsync(provider, completion, username, path, ct);
        return completion;
    }

    private async Task PublishOpenAiCompatibleUsageAsync(
        string provider,
        OpenAiChatCompletionResponse completion,
        string? username,
        string path,
        CancellationToken ct)
    {
        if (completion.Usage is null)
            return;

        try
        {
            await metricsEventPublisher.PublishLlmUsageAsync(
                new LlmUsageRecord(
                    NormalizeTelemetryUsername(username) ?? "system",
                    path,
                    provider,
                    FirstNonEmpty(completion.Model, ResolveAssistantModel(provider), "unknown"),
                    completion.Usage.PromptTokens ?? 0,
                    completion.Usage.CompletionTokens ?? 0,
                    completion.Usage.TotalTokens ?? 0),
                ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to record assistant LLM usage telemetry for {Provider}", provider);
        }
    }

    private async Task PublishAnthropicUsageAsync(
        MessageDeltaUsage? usage,
        string? username,
        string path,
        string model,
        CancellationToken ct)
    {
        var record = BuildAnthropicUsageRecord(usage, username, path, model);
        if (record is null)
            return;

        try
        {
            await metricsEventPublisher.PublishLlmUsageAsync(record, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to record assistant LLM usage telemetry for Anthropic");
        }
    }

    internal static LlmUsageRecord? BuildAnthropicUsageRecord(
        MessageDeltaUsage? usage,
        string? username,
        string path,
        string model)
    {
        if (usage is null)
            return null;

        var inputTokens = SaturatingSum(
            PositiveOrZero(usage.InputTokens),
            PositiveOrZero(usage.CacheCreationInputTokens),
            PositiveOrZero(usage.CacheReadInputTokens));
        var outputTokens = PositiveOrZero(usage.OutputTokens);
        var totalTokens = SaturatingSum(inputTokens, outputTokens);
        if (totalTokens == 0)
            return null;

        return new LlmUsageRecord(
            NormalizeTelemetryUsername(username) ?? "system",
            path,
            "anthropic",
            FirstNonEmpty(model, "unknown"),
            ClampTokenCount(inputTokens),
            ClampTokenCount(outputTokens),
            ClampTokenCount(totalTokens));
    }

    private async Task<HttpRequestMessage> CreateOpenAiCompatibleRequestAsync(
        string provider,
        IReadOnlyList<OpenAiChatMessage> messages,
        IReadOnlyList<OpenAiToolDefinition>? tools)
    {
        var providerOptions = ResolveOpenAiCompatibleOptions(provider);
        var url = BuildOpenAiCompatibleUrl(providerOptions);

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(providerOptions.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerOptions.ApiKey);
        if (!string.IsNullOrWhiteSpace(providerOptions.Organization))
            request.Headers.Add("OpenAI-Organization", providerOptions.Organization);
        if (!string.IsNullOrWhiteSpace(providerOptions.Project))
            request.Headers.Add("OpenAI-Project", providerOptions.Project);

        var body = new OpenAiChatCompletionRequest
        {
            Model = ResolveAssistantModel(provider),
            MaxCompletionTokens = options.Assistant.MaxTokens,
            Messages = await BuildOpenAiCompatibleMessagesAsync(messages),
            Tools = tools?.ToList()
        };

        request.Content = JsonContent.Create(body, options: CodeGraphJsonDefaults.SnakeCase);
        return request;
    }

    private async Task<List<OpenAiChatMessage>> BuildOpenAiCompatibleMessagesAsync(IReadOnlyList<OpenAiChatMessage> messages)
    {
        var result = new List<OpenAiChatMessage>(messages.Count + 1)
        {
            new()
            {
                Role = "system",
                Content = await GetSystemPromptAsync()
            }
        };

        result.AddRange(messages.Select(message => new OpenAiChatMessage
        {
            Role = message.Role,
            Content = message.Content,
            ToolCallId = message.ToolCallId,
            ToolCalls = message.ToolCalls
        }));

        return result;
    }

    private AssistantOpenAiCompatibleOptions ResolveOpenAiCompatibleOptions(string provider)
    {
        return provider switch
        {
            "openai" => new AssistantOpenAiCompatibleOptions
            {
                ApiKey = FirstNonEmpty(options.Assistant.OpenAi.ApiKey, options.OpenAi.ApiKey),
                BaseUrl = FirstNonEmpty(options.Assistant.OpenAi.BaseUrl, options.OpenAi.BaseUrl),
                ChatCompletionsPath = FirstNonEmpty(options.Assistant.OpenAi.ChatCompletionsPath, options.OpenAi.ChatCompletionsPath),
                Organization = FirstNonEmpty(options.Assistant.OpenAi.Organization, options.OpenAi.Organization),
                Project = FirstNonEmpty(options.Assistant.OpenAi.Project, options.OpenAi.Project)
            },
            "local" => new AssistantOpenAiCompatibleOptions
            {
                ApiKey = FirstNonEmpty(options.Assistant.Local.ApiKey, options.Local.ApiKey),
                BaseUrl = FirstNonEmpty(options.Assistant.Local.BaseUrl, options.Local.BaseUrl),
                ChatCompletionsPath = FirstNonEmpty(options.Assistant.Local.ChatCompletionsPath, options.Local.ChatCompletionsPath)
            },
            _ => throw new InvalidOperationException($"Unsupported OpenAI-compatible assistant provider '{provider}'")
        };
    }

    private string BuildOpenAiCompatibleUrl(AssistantOpenAiCompatibleOptions providerOptions)
    {
        var baseUrl = providerOptions.BaseUrl.TrimEnd('/');
        var path = string.IsNullOrWhiteSpace(providerOptions.ChatCompletionsPath)
            ? "/chat/completions"
            : providerOptions.ChatCompletionsPath;
        var relative = path.StartsWith("/") ? path : $"/{path}";
        return $"{baseUrl}{relative}";
    }

    private string ResolveAssistantProvider() =>
        string.IsNullOrWhiteSpace(options.Assistant.Provider)
            ? "anthropic"
            : options.Assistant.Provider.Trim().ToLowerInvariant();

    private string ResolveAssistantModel(string provider) =>
        FirstNonEmpty(
            options.Assistant.Model,
            provider switch
            {
                "openai" => options.OpenAi.Model,
                "local" => options.Local.Model,
                _ => null
            },
            options.Model);

    private static string? TryGetAnthropicBaseUrl(string? messagesApiUrl)
    {
        if (string.IsNullOrWhiteSpace(messagesApiUrl) || !Uri.TryCreate(messagesApiUrl, UriKind.Absolute, out var uri))
            return null;

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string? NormalizeTelemetryUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? null : username.Trim().ToLowerInvariant();

    private static long PositiveOrZero(long? value) =>
        Math.Max(0, value ?? 0);

    private static long SaturatingSum(params long[] values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (value <= 0)
                continue;

            if (long.MaxValue - total < value)
                return long.MaxValue;

            total += value;
        }

        return total;
    }

    private static int ClampTokenCount(long value) =>
        value <= 0 ? 0 : checked((int)Math.Min(value, int.MaxValue));

    private sealed record PendingToolUse(string Id, string Name, StringBuilder Input);

    private sealed class OpenAiChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public int? MaxCompletionTokens { get; set; }
        public List<OpenAiChatMessage> Messages { get; set; } = [];
        public List<OpenAiToolDefinition>? Tools { get; set; }
    }

    private sealed class OpenAiChatCompletionResponse
    {
        public string? Model { get; set; }
        public List<OpenAiChatCompletionChoice> Choices { get; set; } = [];
        public OpenAiChatUsage? Usage { get; set; }
    }

    private sealed class OpenAiChatUsage
    {
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
    }

    private sealed class OpenAiChatCompletionChoice
    {
        public OpenAiChatMessage? Message { get; set; }
    }

    private sealed class OpenAiChatMessage
    {
        public string Role { get; set; } = "";
        public string? Content { get; set; }
        public string? ToolCallId { get; set; }
        public List<OpenAiToolCall>? ToolCalls { get; set; }
    }

    private sealed class OpenAiToolCall
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "function";
        public OpenAiToolFunction? Function { get; set; }
    }

    private sealed class OpenAiToolFunction
    {
        public string? Name { get; set; }
        public string? Arguments { get; set; }
    }

    private sealed class OpenAiToolDefinition
    {
        public string Type { get; set; } = "function";
        public OpenAiFunctionDefinition Function { get; set; } = new();
    }

    private sealed class OpenAiFunctionDefinition
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public OpenAiFunctionParameters Parameters { get; set; } = new();
    }

    private sealed class OpenAiFunctionParameters
    {
        public string Type { get; set; } = "object";
        public Dictionary<string, JsonElement> Properties { get; set; } = [];
        public List<string> Required { get; set; } = [];
    }
}

public record AssistantEvent(string Type, string Content);

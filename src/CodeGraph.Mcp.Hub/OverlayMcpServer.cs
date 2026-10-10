using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Indexer.Client;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;
using CodeGraph.Services.Overlays;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeGraph.Mcp.Hub;

/// <summary>
/// MCP tools that let a personal-access-token holder request a branch overlay index and poll its
/// status. They go through <see cref="IIndexerOperationsService"/>, so in remote-indexer mode the
/// request is forwarded to the indexer host exactly like <c>POST /api/indexer/overlays</c>.
/// Neither tool calls a model. Access is governed by the per-token tool entitlement
/// (McpToolEntitlementMiddleware), like every other native tool.
/// </summary>
[McpServerToolType]
public sealed class OverlayMcpServer(
    IIndexerOperationsService indexer,
    IHttpContextAccessor httpContextAccessor,
    ICommitScopeResolver? commitScopes = null)
{
    public const string RequestOverlayTool = "request_overlay";
    public const string GetOverlayStatusTool = "get_overlay_status";

    public const string RepoNotFoundCode = "repo_not_found";
    public const string InvalidRefCode = "invalid_ref";
    public const string OverlayNotFoundCode = "overlay_not_found";
    public const string InvalidRequestCode = "invalid_request";
    public const string OverlaysUnavailableCode = "overlays_unavailable";
    public const string TemporarilyUnavailableCode = "temporarily_unavailable";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [McpServerTool(Name = RequestOverlayTool, Title = "Request Branch Overlay", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Request a branch overlay index for a repository at a branch name or full commit SHA. The ref is resolved " +
        "to its current commit; if a live overlay (queued, indexing, or ready against the current default-branch " +
        "index, within its TTL) already exists for that repository and commit it is returned, otherwise a new one " +
        "is queued. Answers overlayId, status, headSha and baseSha. Poll get_overlay_status until status is ready, " +
        "then pass headSha as the sha parameter of the read tools. No model is called.")]
    public async Task<CallToolResult> RequestOverlay(
        [Description("Indexed repository name.")] string repo,
        [Description("Branch name (e.g. factory/feature-x), tag, or full 40-hex commit SHA.")] string @ref,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
            return Error(InvalidRequestCode, "repo is required.");
        if (string.IsNullOrWhiteSpace(@ref))
            return Error(InvalidRefCode, "ref is required: a branch name, tag, or full commit SHA.");

        BranchOverlayResponse overlay;
        try
        {
            overlay = await indexer.StartBranchOverlayAsync(
                Username,
                new BranchOverlayRequest { Repo = repo.Trim(), Ref = @ref.Trim(), ReuseLive = true },
                cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(RepoNotFoundCode, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error(InvalidRefCode, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return Error(OverlaysUnavailableCode, ex.Message);
        }
        catch (Exception ex) when (ex is BranchOverlayTransientException or TimeoutException)
        {
            return Error(TemporarilyUnavailableCode, ex.Message);
        }
        catch (Exception ex) when (IsRemoteIndexerFailure(ex))
        {
            return Error(TemporarilyUnavailableCode, ex.Message);
        }

        return await AnswerAsync(overlay, overlay.Reused, cancellationToken);
    }

    [McpServerTool(Name = GetOverlayStatusTool, Title = "Get Branch Overlay Status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Status of a branch overlay index: queued, indexing, ready, failed, or expired, with headSha, baseSha, " +
        "error, and change counts. stale is true for a ready overlay whose base is no longer the default-branch " +
        "index commit (read tools will not use it; request a new overlay). No model is called.")]
    public async Task<CallToolResult> GetOverlayStatus(
        [Description("Overlay id returned by request_overlay.")] long overlayId,
        CancellationToken cancellationToken = default)
    {
        if (overlayId <= 0)
            return Error(InvalidRequestCode, "overlayId must be a positive overlay id.");

        BranchOverlayResponse? overlay;
        try
        {
            overlay = await indexer.GetBranchOverlayAsync(overlayId, cancellationToken);
        }
        catch (NotSupportedException ex)
        {
            return Error(OverlaysUnavailableCode, ex.Message);
        }
        catch (Exception ex) when (IsRemoteIndexerFailure(ex))
        {
            return Error(TemporarilyUnavailableCode, ex.Message);
        }

        return overlay is null
            ? Error(OverlayNotFoundCode, $"Branch overlay {overlayId} was not found.")
            : await AnswerAsync(overlay, reused: null, cancellationToken);
    }

    private async Task<CallToolResult> AnswerAsync(BranchOverlayResponse overlay, bool? reused, CancellationToken ct)
    {
        var stale = await IsStaleAsync(overlay, ct);
        var structured = new JsonObject
        {
            ["commitSha"] = overlay.HeadSha,
            ["overlayId"] = overlay.OverlayId,
            ["repo"] = overlay.Repo,
            ["ref"] = overlay.Ref,
            ["status"] = overlay.Status,
            ["headSha"] = overlay.HeadSha,
            ["baseSha"] = overlay.BaseSha,
            ["stale"] = stale,
            ["overlayError"] = overlay.Error,
            ["changedFileCount"] = overlay.ChangedFileCount,
            ["nodeCount"] = overlay.NodeCount,
            ["edgeCount"] = overlay.EdgeCount,
            ["createdAt"] = overlay.CreatedAt,
            ["completedAt"] = overlay.CompletedAt,
            ["expiresAt"] = overlay.ExpiresAt,
            ["expiredAt"] = overlay.ExpiredAt
        };
        if (reused is not null)
            structured["reused"] = reused.Value;

        var lines = new List<string>
        {
            $"Commit: {overlay.HeadSha ?? "unknown (resolved when indexing starts)"}",
            "",
            $"Overlay {overlay.OverlayId} for {overlay.Repo}@{overlay.Ref}: {overlay.Status}" +
            (reused switch { true => " (existing overlay reused)", false => " (new overlay queued)", _ => "" }) + ".",
            $"Head: {overlay.HeadSha ?? "not resolved yet"}",
            $"Base (default-branch index commit): {overlay.BaseSha ?? "not resolved yet"}"
        };
        switch (overlay.Status)
        {
            case BranchOverlayStatuses.Ready when stale:
                lines.Add("Stale: the default-branch index has moved past this overlay's base, so read tools will not use it. Request a new overlay.");
                break;
            case BranchOverlayStatuses.Ready:
                lines.Add($"Ready: {overlay.ChangedFileCount} changed files, {overlay.NodeCount} nodes. Pass sha={overlay.HeadSha} to the read tools.");
                break;
            case BranchOverlayStatuses.Failed:
                lines.Add($"Failed: {overlay.Error ?? "no error recorded"}. Request it again to queue a new overlay.");
                break;
            case BranchOverlayStatuses.Expired:
                lines.Add($"Expired at {overlay.ExpiredAt:u}; its data was deleted. Request it again to queue a new overlay.");
                break;
            default:
                lines.Add($"Not ready yet; poll {GetOverlayStatusTool}(overlayId: {overlay.OverlayId}). Expires at {overlay.ExpiresAt:u}.");
                break;
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = string.Join("\n", lines) + "\n" }],
            StructuredContent = JsonSerializer.SerializeToElement(structured, JsonOptions)
        };
    }

    /// <summary>Same rule as the query side: a ready overlay is used only while its base is the default-branch index commit.</summary>
    private async Task<bool> IsStaleAsync(BranchOverlayResponse overlay, CancellationToken ct)
    {
        if (overlay.Status != BranchOverlayStatuses.Ready || commitScopes is null || overlay.BaseSha is null)
            return false;

        var current = await commitScopes.GetDefaultCommitAsync(overlay.Repo, ct);
        return current is not null && !string.Equals(current, overlay.BaseSha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Remote-indexer mode: the indexer host is unreachable, or answered with an error the
    /// operations service does not map to a typed exception.
    /// </summary>
    private static bool IsRemoteIndexerFailure(Exception ex)
        => ex is HttpRequestException or IndexerClientException;

    private static CallToolResult Error(string code, string message)
    {
        var structured = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Error ({code}): {message}" }],
            StructuredContent = JsonSerializer.SerializeToElement(structured, JsonOptions),
            IsError = true
        };
    }

    private string Username =>
        httpContextAccessor.HttpContext?.User.FindFirstValue("preferred_username")
        ?? httpContextAccessor.HttpContext?.User.FindFirstValue("username")
        ?? httpContextAccessor.HttpContext?.User.Identity?.Name
        ?? "mcp";
}

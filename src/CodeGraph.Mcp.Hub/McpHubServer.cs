using System.ComponentModel;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;

namespace CodeGraph.Mcp.Hub;

[McpServerToolType]
public sealed class McpHubServer(McpHubService hub, IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "mcp_hub_catalog", Title = "MCP Hub Catalog", ReadOnly = true)]
    [Description("List MCP Hub providers and tools, including enabled state and entitlement metadata.")]
    public async Task<string> Catalog(CancellationToken cancellationToken)
    {
        var catalog = await hub.GetCatalogAsync(cancellationToken);
        return System.Text.Json.JsonSerializer.Serialize(catalog, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
    }

    [McpServerTool(Name = "shortcut_search_epics", Title = "Search Shortcut Epics", ReadOnly = true)]
    [Description("Search Shortcut epics through your connected (delegated) Shortcut provider credential.")]
    public async Task<string> SearchShortcutEpics(
        [Description("Optional Shortcut search query.")] string? query = null,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("shortcut", "shortcut_search_epics", "search", "epics", "delegated", () => hub.SearchShortcutEpicsAsync(query, Username, cancellationToken), cancellationToken);

    [McpServerTool(Name = "shortcut_search_stories", Title = "Search Shortcut Stories", ReadOnly = true)]
    [Description("Search Shortcut stories through your connected (delegated) Shortcut provider credential.")]
    public async Task<string> SearchShortcutStories(
        [Description("Optional Shortcut search query.")] string? query = null,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("shortcut", "shortcut_search_stories", "search", "stories", "delegated", () => hub.SearchShortcutStoriesAsync(query, Username, cancellationToken), cancellationToken);

    [McpServerTool(Name = "stories-get-by-id", Title = "Get Shortcut Story", ReadOnly = true)]
    [Description("Get a Shortcut story by public ID.")]
    public Task<string> GetShortcutStory(int storyPublicId, bool full = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("stories-get-by-id", $"stories/{storyPublicId}", "get", $"story:{storyPublicId}", cancellationToken);

    [McpServerTool(Name = "stories-get-history", Title = "Get Shortcut Story History", ReadOnly = true)]
    [Description("Get the change history for a Shortcut story.")]
    public Task<string> GetShortcutStoryHistory(int storyPublicId, CancellationToken cancellationToken = default) =>
        ShortcutGet("stories-get-history", $"stories/{storyPublicId}/history", "history", $"story:{storyPublicId}", cancellationToken);

    [McpServerTool(Name = "stories-search", Title = "Search Shortcut Stories", ReadOnly = true)]
    [Description("Find Shortcut stories. Pass any Shortcut search syntax in query, for example owner:me is:started.")]
    public Task<string> SearchShortcutStoriesV3(string? query = null, string? nextPageToken = null, CancellationToken cancellationToken = default) =>
        ShortcutGet("stories-search", "search/stories", "search", "stories", cancellationToken, ("query", query), ("next", nextPageToken));

    [McpServerTool(Name = "stories-get-branch-name", Title = "Get Shortcut Story Branch Name", ReadOnly = true)]
    [Description("Get the recommended git branch name for a story.")]
    public Task<string> GetShortcutStoryBranchName(int storyPublicId, CancellationToken cancellationToken = default) =>
        InvokeAuditedAsync("shortcut", "stories-get-branch-name", "get", $"story:{storyPublicId}", "delegated", async () =>
        {
            var storyJson = await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, $"stories/{storyPublicId}", ct: cancellationToken);
            using var storyDoc = JsonDocument.Parse(storyJson);
            if (TryGetString(storyDoc.RootElement, "formatted_vcs_branch_name") is { Length: > 0 } existing)
                return existing;

            var memberJson = await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, "member", ct: cancellationToken);
            using var memberDoc = JsonDocument.Parse(memberJson);
            var mention = TryGetString(memberDoc.RootElement, "mention_name") ?? "shortcut";
            var name = TryGetString(storyDoc.RootElement, "name") ?? $"story-{storyPublicId}";
            return $"{mention}/sc-{storyPublicId}/{Slugify(name)}"[..Math.Min(50, $"{mention}/sc-{storyPublicId}/{Slugify(name)}".Length)];
        }, cancellationToken);

    [McpServerTool(Name = "stories-create", Title = "Create Shortcut Story", ReadOnly = false, Destructive = false)]
    [Description("Create a Shortcut story. bodyJson is the Shortcut CreateStory request JSON object.")]
    public Task<string> CreateShortcutStory(string bodyJson, CancellationToken cancellationToken = default) =>
        ShortcutSend("stories-create", HttpMethod.Post, "stories", bodyJson, "create", "stories", cancellationToken);

    [McpServerTool(Name = "stories-update", Title = "Update Shortcut Story", ReadOnly = false, Destructive = false)]
    [Description("Update a Shortcut story. bodyJson is the Shortcut UpdateStory request JSON object; nullable fields can be set to null.")]
    public Task<string> UpdateShortcutStory(int storyPublicId, string bodyJson, CancellationToken cancellationToken = default) =>
        ShortcutSend("stories-update", HttpMethod.Put, $"stories/{storyPublicId}", bodyJson, "update", $"story:{storyPublicId}", cancellationToken);

    [McpServerTool(Name = "stories-upload-file", Title = "Upload Shortcut Story File", ReadOnly = false, Destructive = false)]
    [Description("Upload a local file and attach it to a Shortcut story.")]
    public Task<string> UploadShortcutStoryFile(int storyPublicId, string filePath, CancellationToken cancellationToken = default) =>
        InvokeAuditedAsync("shortcut", "stories-upload-file", "upload", $"story:{storyPublicId}", "delegated",
            () => hub.UploadShortcutFileAsync(Username, storyPublicId, filePath, cancellationToken), cancellationToken);

    [McpServerTool(Name = "stories-assign-current-user", Title = "Assign Current User To Shortcut Story", ReadOnly = false, Destructive = false)]
    [Description("Assign the current Shortcut API user as a story owner.")]
    public Task<string> AssignCurrentUserToShortcutStory(int storyPublicId, CancellationToken cancellationToken = default) =>
        ChangeCurrentUserAssignment(storyPublicId, assign: true, "stories-assign-current-user", cancellationToken);

    [McpServerTool(Name = "stories-unassign-current-user", Title = "Unassign Current User From Shortcut Story", ReadOnly = false, Destructive = false)]
    [Description("Remove the current Shortcut API user from a story's owners.")]
    public Task<string> UnassignCurrentUserFromShortcutStory(int storyPublicId, CancellationToken cancellationToken = default) =>
        ChangeCurrentUserAssignment(storyPublicId, assign: false, "stories-unassign-current-user", cancellationToken);

    [McpServerTool(Name = "stories-create-comment", Title = "Create Shortcut Story Comment", ReadOnly = false, Destructive = false)]
    [Description("Create a comment on a Shortcut story.")]
    public Task<string> CreateShortcutStoryComment(int storyPublicId, string text, int? replyToCommentId = null, CancellationToken cancellationToken = default) =>
        ShortcutSend("stories-create-comment", HttpMethod.Post, $"stories/{storyPublicId}/comments",
            McpHubService.JsonBody(("text", text), ("parent_id", replyToCommentId)), "comment", $"story:{storyPublicId}", cancellationToken);

    [McpServerTool(Name = "stories-create-subtask", Title = "Create Shortcut Story Subtask", ReadOnly = false, Destructive = false)]
    [Description("Create a new story as a sub-task of another story. bodyJson is merged with parent_story_id.")]
    public Task<string> CreateShortcutStorySubtask(int parentStoryPublicId, string bodyJson, CancellationToken cancellationToken = default) =>
        InvokeAuditedAsync("shortcut", "stories-create-subtask", "create", $"story:{parentStoryPublicId}", "delegated", () =>
            hub.InvokeShortcutApiAsync(Username, HttpMethod.Post, "stories", MergeJson(bodyJson, ("parent_story_id", parentStoryPublicId)), ct: cancellationToken), cancellationToken);

    [McpServerTool(Name = "stories-add-subtask", Title = "Add Shortcut Story Subtask", ReadOnly = false, Destructive = false)]
    [Description("Add an existing story as a sub-task of another story.")]
    public Task<string> AddShortcutStorySubtask(int parentStoryPublicId, int subTaskPublicId, CancellationToken cancellationToken = default) =>
        ShortcutSend("stories-add-subtask", HttpMethod.Put, $"stories/{subTaskPublicId}",
            McpHubService.JsonBody(("parent_story_id", parentStoryPublicId)), "update", $"story:{subTaskPublicId}", cancellationToken);

    [McpServerTool(Name = "stories-remove-subtask", Title = "Remove Shortcut Story Subtask", ReadOnly = false, Destructive = false)]
    [Description("Remove a story from its parent story.")]
    public Task<string> RemoveShortcutStorySubtask(int subTaskPublicId, CancellationToken cancellationToken = default) =>
        ShortcutSend("stories-remove-subtask", HttpMethod.Put, $"stories/{subTaskPublicId}", """{"parent_story_id":null}""", "update", $"story:{subTaskPublicId}", cancellationToken);

    [McpServerTool(Name = "stories-add-task", Title = "Add Shortcut Story Task", ReadOnly = false, Destructive = false)]
    [Description("Add a checklist task to a story. ownerIdsJson is an optional JSON string array.")]
    public Task<string> AddShortcutStoryTask(int storyPublicId, string taskDescription, string? ownerIdsJson = null, CancellationToken cancellationToken = default) =>
        ShortcutSend("stories-add-task", HttpMethod.Post, $"stories/{storyPublicId}/tasks",
            MergeJson(McpHubService.JsonBody(("description", taskDescription)), ("owner_ids", JsonArrayOrNull(ownerIdsJson))), "task", $"story:{storyPublicId}", cancellationToken);

    [McpServerTool(Name = "stories-update-task", Title = "Update Shortcut Story Task", ReadOnly = false, Destructive = false)]
    [Description("Update a checklist task on a story. bodyJson is the Shortcut UpdateTask request JSON object.")]
    public Task<string> UpdateShortcutStoryTask(int storyPublicId, int taskPublicId, string bodyJson, CancellationToken cancellationToken = default) =>
        ShortcutSend("stories-update-task", HttpMethod.Put, $"stories/{storyPublicId}/tasks/{taskPublicId}", bodyJson, "task", $"story:{storyPublicId}", cancellationToken);

    [McpServerTool(Name = "stories-add-relation", Title = "Add Shortcut Story Relation", ReadOnly = false, Destructive = false)]
    [Description("Add a relationship between two Shortcut stories. relationshipType supports relates to, blocks, blocked by, duplicates, duplicated by.")]
    public Task<string> AddShortcutStoryRelation(int storyPublicId, int relatedStoryPublicId, string relationshipType = "relates to", CancellationToken cancellationToken = default)
    {
        var (subject, obj, verb) = relationshipType.Trim().ToLowerInvariant() switch
        {
            "blocks" => (storyPublicId, relatedStoryPublicId, "blocks"),
            "blocked by" => (relatedStoryPublicId, storyPublicId, "blocks"),
            "duplicates" => (storyPublicId, relatedStoryPublicId, "duplicates"),
            "duplicated by" => (relatedStoryPublicId, storyPublicId, "duplicates"),
            _ => (storyPublicId, relatedStoryPublicId, "relates to")
        };
        return ShortcutSend("stories-add-relation", HttpMethod.Post, "story-links",
            McpHubService.JsonBody(("subject_id", subject), ("object_id", obj), ("verb", verb)), "relation", $"story:{storyPublicId}", cancellationToken);
    }

    [McpServerTool(Name = "stories-add-external-link", Title = "Add Shortcut Story External Link", ReadOnly = false, Destructive = false)]
    [Description("Add an external link to a Shortcut story.")]
    public Task<string> AddShortcutStoryExternalLink(int storyPublicId, string externalLink, CancellationToken cancellationToken = default) =>
        ChangeStoryExternalLinks(storyPublicId, "stories-add-external-link", links => links.Concat([externalLink]).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);

    [McpServerTool(Name = "stories-remove-external-link", Title = "Remove Shortcut Story External Link", ReadOnly = false, Destructive = true)]
    [Description("Remove an external link from a Shortcut story.")]
    public Task<string> RemoveShortcutStoryExternalLink(int storyPublicId, string externalLink, CancellationToken cancellationToken = default) =>
        ChangeStoryExternalLinks(storyPublicId, "stories-remove-external-link", links => links.Where(link => !string.Equals(link, externalLink, StringComparison.Ordinal)).ToArray(), cancellationToken);

    [McpServerTool(Name = "stories-set-external-links", Title = "Set Shortcut Story External Links", ReadOnly = false, Destructive = true)]
    [Description("Replace all external links on a Shortcut story. externalLinksJson must be a JSON string array.")]
    public Task<string> SetShortcutStoryExternalLinks(int storyPublicId, string externalLinksJson, CancellationToken cancellationToken = default) =>
        ChangeStoryExternalLinks(storyPublicId, "stories-set-external-links", _ => JsonStringArray(externalLinksJson), cancellationToken);

    [McpServerTool(Name = "stories-get-by-external-link", Title = "Get Shortcut Stories By External Link", ReadOnly = true)]
    [Description("Find Shortcut stories that contain a specific external link.")]
    public Task<string> GetShortcutStoriesByExternalLink(string externalLink, CancellationToken cancellationToken = default) =>
        ShortcutGet("stories-get-by-external-link", "external-link/stories", "search", externalLink, cancellationToken, ("external_link", externalLink));

    [McpServerTool(Name = "epics-get-by-id", Title = "Get Shortcut Epic", ReadOnly = true)]
    [Description("Get a Shortcut epic by public ID.")]
    public Task<string> GetShortcutEpic(int epicPublicId, bool full = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("epics-get-by-id", $"epics/{epicPublicId}", "get", $"epic:{epicPublicId}", cancellationToken);

    [McpServerTool(Name = "epics-search", Title = "Search Shortcut Epics", ReadOnly = true)]
    [Description("Find Shortcut epics. Pass Shortcut search syntax in query.")]
    public Task<string> SearchShortcutEpicsV3(string? query = null, string? nextPageToken = null, CancellationToken cancellationToken = default) =>
        ShortcutGet("epics-search", "search/epics", "search", "epics", cancellationToken, ("query", query), ("next", nextPageToken));

    [McpServerTool(Name = "epics-create", Title = "Create Shortcut Epic", ReadOnly = false, Destructive = false)]
    [Description("Create a Shortcut epic. bodyJson is the Shortcut CreateEpic request JSON object.")]
    public Task<string> CreateShortcutEpic(string bodyJson, CancellationToken cancellationToken = default) =>
        ShortcutSend("epics-create", HttpMethod.Post, "epics", bodyJson, "create", "epics", cancellationToken);

    [McpServerTool(Name = "epics-update", Title = "Update Shortcut Epic", ReadOnly = false, Destructive = false)]
    [Description("Update a Shortcut epic. bodyJson is the Shortcut UpdateEpic request JSON object.")]
    public Task<string> UpdateShortcutEpic(int epicPublicId, string bodyJson, CancellationToken cancellationToken = default) =>
        ShortcutSend("epics-update", HttpMethod.Put, $"epics/{epicPublicId}", bodyJson, "update", $"epic:{epicPublicId}", cancellationToken);

    [McpServerTool(Name = "epics-delete", Title = "Delete Shortcut Epic", ReadOnly = false, Destructive = true)]
    [Description("Delete a Shortcut epic.")]
    public Task<string> DeleteShortcutEpic(int epicPublicId, CancellationToken cancellationToken = default) =>
        ShortcutSend("epics-delete", HttpMethod.Delete, $"epics/{epicPublicId}", null, "delete", $"epic:{epicPublicId}", cancellationToken);

    [McpServerTool(Name = "epics-create-comment", Title = "Create Shortcut Epic Comment", ReadOnly = false, Destructive = false)]
    [Description("Create a comment on a Shortcut epic.")]
    public Task<string> CreateShortcutEpicComment(int epicPublicId, string text, int? replyToCommentId = null, CancellationToken cancellationToken = default) =>
        ShortcutSend("epics-create-comment", HttpMethod.Post,
            replyToCommentId is null ? $"epics/{epicPublicId}/comments" : $"epics/{epicPublicId}/comments/{replyToCommentId}",
            McpHubService.JsonBody(("text", text)), "comment", $"epic:{epicPublicId}", cancellationToken);

    [McpServerTool(Name = "iterations-get-stories", Title = "Get Shortcut Iteration Stories", ReadOnly = true)]
    [Description("Get stories in a Shortcut iteration.")]
    public Task<string> GetShortcutIterationStories(int iterationPublicId, bool includeStoryDescriptions = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("iterations-get-stories", $"iterations/{iterationPublicId}/stories", "list", $"iteration:{iterationPublicId}", cancellationToken, ("includes_description", includeStoryDescriptions));

    [McpServerTool(Name = "iterations-get-by-id", Title = "Get Shortcut Iteration", ReadOnly = true)]
    [Description("Get a Shortcut iteration by public ID.")]
    public Task<string> GetShortcutIteration(int iterationPublicId, bool full = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("iterations-get-by-id", $"iterations/{iterationPublicId}", "get", $"iteration:{iterationPublicId}", cancellationToken);

    [McpServerTool(Name = "iterations-search", Title = "Search Shortcut Iterations", ReadOnly = true)]
    [Description("Find Shortcut iterations. Pass Shortcut search syntax in query.")]
    public Task<string> SearchShortcutIterations(string? query = null, string? nextPageToken = null, CancellationToken cancellationToken = default) =>
        ShortcutGet("iterations-search", "search/iterations", "search", "iterations", cancellationToken, ("query", query), ("next", nextPageToken));

    [McpServerTool(Name = "iterations-create", Title = "Create Shortcut Iteration", ReadOnly = false, Destructive = false)]
    [Description("Create a Shortcut iteration. bodyJson is the Shortcut CreateIteration request JSON object.")]
    public Task<string> CreateShortcutIteration(string bodyJson, CancellationToken cancellationToken = default) =>
        ShortcutSend("iterations-create", HttpMethod.Post, "iterations", bodyJson, "create", "iterations", cancellationToken);

    [McpServerTool(Name = "iterations-update", Title = "Update Shortcut Iteration", ReadOnly = false, Destructive = false)]
    [Description("Update a Shortcut iteration. bodyJson is the Shortcut UpdateIteration request JSON object.")]
    public Task<string> UpdateShortcutIteration(int iterationPublicId, string bodyJson, CancellationToken cancellationToken = default) =>
        ShortcutSend("iterations-update", HttpMethod.Put, $"iterations/{iterationPublicId}", bodyJson, "update", $"iteration:{iterationPublicId}", cancellationToken);

    [McpServerTool(Name = "iterations-delete", Title = "Delete Shortcut Iteration", ReadOnly = false, Destructive = true)]
    [Description("Delete a Shortcut iteration.")]
    public Task<string> DeleteShortcutIteration(int iterationPublicId, CancellationToken cancellationToken = default) =>
        ShortcutSend("iterations-delete", HttpMethod.Delete, $"iterations/{iterationPublicId}", null, "delete", $"iteration:{iterationPublicId}", cancellationToken);

    [McpServerTool(Name = "iterations-get-active", Title = "Get Active Shortcut Iterations", ReadOnly = true)]
    [Description("Get active iterations, optionally filtered by team search term.")]
    public Task<string> GetActiveShortcutIterations(string? team = null, CancellationToken cancellationToken = default) =>
        ShortcutGet("iterations-get-active", "search/iterations", "search", "iterations:active", cancellationToken, ("query", BuildShortcutSearch("is:started", team)));

    [McpServerTool(Name = "iterations-get-upcoming", Title = "Get Upcoming Shortcut Iterations", ReadOnly = true)]
    [Description("Get upcoming iterations, optionally filtered by team search term.")]
    public Task<string> GetUpcomingShortcutIterations(string? team = null, CancellationToken cancellationToken = default) =>
        ShortcutGet("iterations-get-upcoming", "search/iterations", "search", "iterations:upcoming", cancellationToken, ("query", BuildShortcutSearch("is:unstarted", team)));

    [McpServerTool(Name = "objectives-get-by-id", Title = "Get Shortcut Objective", ReadOnly = true)]
    [Description("Get a Shortcut objective by public ID.")]
    public Task<string> GetShortcutObjective(int objectivePublicId, bool full = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("objectives-get-by-id", $"milestones/{objectivePublicId}", "get", $"objective:{objectivePublicId}", cancellationToken);

    [McpServerTool(Name = "objectives-search", Title = "Search Shortcut Objectives", ReadOnly = true)]
    [Description("Find Shortcut objectives. Pass Shortcut search syntax in query.")]
    public Task<string> SearchShortcutObjectives(string? query = null, string? nextPageToken = null, CancellationToken cancellationToken = default) =>
        ShortcutGet("objectives-search", "search/objectives", "search", "objectives", cancellationToken, ("query", query), ("next", nextPageToken));

    [McpServerTool(Name = "teams-get-by-id", Title = "Get Shortcut Team", ReadOnly = true)]
    [Description("Get a Shortcut team by ID.")]
    public Task<string> GetShortcutTeam(string teamPublicId, bool full = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("teams-get-by-id", $"groups/{Uri.EscapeDataString(teamPublicId)}", "get", $"team:{teamPublicId}", cancellationToken);

    [McpServerTool(Name = "teams-list", Title = "List Shortcut Teams", ReadOnly = true)]
    [Description("List Shortcut teams.")]
    public Task<string> ListShortcutTeams(bool includeArchived = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("teams-list", "groups", "list", "teams", cancellationToken, ("archived", includeArchived));

    [McpServerTool(Name = "projects-list", Title = "List Shortcut Projects", ReadOnly = true)]
    [Description("List Shortcut projects.")]
    public Task<string> ListShortcutProjects(bool includeArchived = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("projects-list", "projects", "list", "projects", cancellationToken);

    [McpServerTool(Name = "projects-get-by-id", Title = "Get Shortcut Project", ReadOnly = true)]
    [Description("Get a Shortcut project by public ID.")]
    public Task<string> GetShortcutProject(int projectPublicId, CancellationToken cancellationToken = default) =>
        ShortcutGet("projects-get-by-id", $"projects/{projectPublicId}", "get", $"project:{projectPublicId}", cancellationToken);

    [McpServerTool(Name = "projects-get-stories", Title = "Get Shortcut Project Stories", ReadOnly = true)]
    [Description("Get stories in a Shortcut project.")]
    public Task<string> GetShortcutProjectStories(int projectPublicId, CancellationToken cancellationToken = default) =>
        ShortcutGet("projects-get-stories", "search/stories", "search", $"project:{projectPublicId}", cancellationToken, ("query", $"project:{projectPublicId}"));

    [McpServerTool(Name = "workflows-get-default", Title = "Get Default Shortcut Workflow", ReadOnly = true)]
    [Description("Get the default Shortcut workflow for a team or workspace.")]
    public Task<string> GetDefaultShortcutWorkflow(string? teamPublicId = null, CancellationToken cancellationToken = default) =>
        InvokeAuditedAsync("shortcut", "workflows-get-default", "get", teamPublicId ?? "workspace", "delegated", async () =>
        {
            if (!string.IsNullOrWhiteSpace(teamPublicId))
            {
                var teamJson = await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, $"groups/{Uri.EscapeDataString(teamPublicId)}", ct: cancellationToken);
                using var teamDoc = JsonDocument.Parse(teamJson);
                if (TryGetInt(teamDoc.RootElement, "default_workflow_id") is { } teamWorkflowId)
                    return await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, $"workflows/{teamWorkflowId}", ct: cancellationToken);
            }

            var memberJson = await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, "member", ct: cancellationToken);
            using var memberDoc = JsonDocument.Parse(memberJson);
            if (memberDoc.RootElement.TryGetProperty("workspace2", out var workspace)
                && TryGetInt(workspace, "default_workflow_id") is { } workflowId)
                return await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, $"workflows/{workflowId}", ct: cancellationToken);

            return """{"message":"No default workflow found."}""";
        }, cancellationToken);

    [McpServerTool(Name = "workflows-get-by-id", Title = "Get Shortcut Workflow", ReadOnly = true)]
    [Description("Get a Shortcut workflow by public ID.")]
    public Task<string> GetShortcutWorkflow(int workflowPublicId, bool full = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("workflows-get-by-id", $"workflows/{workflowPublicId}", "get", $"workflow:{workflowPublicId}", cancellationToken);

    [McpServerTool(Name = "workflows-list", Title = "List Shortcut Workflows", ReadOnly = true)]
    [Description("List Shortcut workflows.")]
    public Task<string> ListShortcutWorkflows(CancellationToken cancellationToken = default) =>
        ShortcutGet("workflows-list", "workflows", "list", "workflows", cancellationToken);

    [McpServerTool(Name = "users-get-current", Title = "Get Current Shortcut User", ReadOnly = true)]
    [Description("Get the current Shortcut API user.")]
    public Task<string> GetCurrentShortcutUser(CancellationToken cancellationToken = default) =>
        ShortcutGet("users-get-current", "member", "get", "member", cancellationToken);

    [McpServerTool(Name = "users-get-current-teams", Title = "Get Current Shortcut User Teams", ReadOnly = true)]
    [Description("Get the current Shortcut user's teams.")]
    public Task<string> GetCurrentShortcutUserTeams(CancellationToken cancellationToken = default) =>
        InvokeAuditedAsync("shortcut", "users-get-current-teams", "list", "member:teams", "delegated", async () =>
        {
            var memberJson = await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, "member", ct: cancellationToken);
            using var memberDoc = JsonDocument.Parse(memberJson);
            var userId = TryGetString(memberDoc.RootElement, "id");
            var teamsJson = await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, "groups", ct: cancellationToken);
            if (userId is null)
                return teamsJson;
            using var teamsDoc = JsonDocument.Parse(teamsJson);
            var teams = teamsDoc.RootElement.EnumerateArray()
                .Where(team => team.TryGetProperty("member_ids", out var ids) && ids.EnumerateArray().Any(id => id.GetString() == userId))
                .Select(team => team.Clone())
                .ToArray();
            return JsonSerializer.Serialize(teams);
        }, cancellationToken);

    [McpServerTool(Name = "users-list", Title = "List Shortcut Users", ReadOnly = true)]
    [Description("List Shortcut workspace users.")]
    public Task<string> ListShortcutUsers(CancellationToken cancellationToken = default) =>
        ShortcutGet("users-list", "members", "list", "members", cancellationToken);

    [McpServerTool(Name = "labels-list", Title = "List Shortcut Labels", ReadOnly = true)]
    [Description("List Shortcut labels.")]
    public Task<string> ListShortcutLabels(bool includeArchived = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("labels-list", "labels", "list", "labels", cancellationToken);

    [McpServerTool(Name = "labels-get-stories", Title = "Get Shortcut Label Stories", ReadOnly = true)]
    [Description("Get stories with a specific Shortcut label.")]
    public Task<string> GetShortcutLabelStories(int labelPublicId, CancellationToken cancellationToken = default) =>
        ShortcutGet("labels-get-stories", $"labels/{labelPublicId}/stories", "list", $"label:{labelPublicId}", cancellationToken);

    [McpServerTool(Name = "labels-create", Title = "Create Shortcut Label", ReadOnly = false, Destructive = false)]
    [Description("Create a Shortcut label.")]
    public Task<string> CreateShortcutLabel(string name, string? color = null, string? description = null, CancellationToken cancellationToken = default) =>
        ShortcutSend("labels-create", HttpMethod.Post, "labels",
            McpHubService.JsonBody(("name", name), ("color", color), ("description", description)), "create", "labels", cancellationToken);

    [McpServerTool(Name = "custom-fields-list", Title = "List Shortcut Custom Fields", ReadOnly = true)]
    [Description("List Shortcut custom fields and enum values.")]
    public Task<string> ListShortcutCustomFields(bool includeDisabled = false, CancellationToken cancellationToken = default) =>
        ShortcutGet("custom-fields-list", "custom-fields", "list", "custom-fields", cancellationToken);

    [McpServerTool(Name = "documents-create", Title = "Create Shortcut Document", ReadOnly = false, Destructive = false)]
    [Description("Create a Shortcut document with Markdown content.")]
    public Task<string> CreateShortcutDocument(string title, string content, CancellationToken cancellationToken = default) =>
        ShortcutSend("documents-create", HttpMethod.Post, "documents",
            McpHubService.JsonBody(("title", title), ("content", content), ("content_format", "markdown")), "create", "documents", cancellationToken);

    [McpServerTool(Name = "documents-update", Title = "Update Shortcut Document", ReadOnly = false, Destructive = false)]
    [Description("Update a Shortcut document. bodyJson is the Shortcut UpdateDoc request JSON object.")]
    public Task<string> UpdateShortcutDocument(string docId, string bodyJson, CancellationToken cancellationToken = default) =>
        ShortcutSend("documents-update", HttpMethod.Put, $"documents/{Uri.EscapeDataString(docId)}", bodyJson, "update", $"document:{docId}", cancellationToken);

    [McpServerTool(Name = "documents-list", Title = "List Shortcut Documents", ReadOnly = true)]
    [Description("List Shortcut documents visible to the current user.")]
    public Task<string> ListShortcutDocuments(CancellationToken cancellationToken = default) =>
        ShortcutGet("documents-list", "documents", "list", "documents", cancellationToken);

    [McpServerTool(Name = "documents-search", Title = "Search Shortcut Documents", ReadOnly = true)]
    [Description("Search Shortcut documents.")]
    public Task<string> SearchShortcutDocuments(string title, string? nextPageToken = null, bool? archived = null, bool? createdByCurrentUser = null, bool? followedByCurrentUser = null, CancellationToken cancellationToken = default) =>
        ShortcutGet("documents-search", "search/documents", "search", "documents", cancellationToken,
            ("title", title), ("next", nextPageToken), ("archived", archived), ("created_by_me", createdByCurrentUser), ("followed_by_me", followedByCurrentUser));

    [McpServerTool(Name = "documents-get-by-id", Title = "Get Shortcut Document", ReadOnly = true)]
    [Description("Get a Shortcut document by ID, returning Markdown content by default.")]
    public Task<string> GetShortcutDocument(string docId, CancellationToken cancellationToken = default) =>
        ShortcutGet("documents-get-by-id", $"documents/{Uri.EscapeDataString(docId)}", "get", $"document:{docId}", cancellationToken, ("content_format", "markdown"));

    [McpServerTool(Name = "rabbitmq_list_queues", Title = "List RabbitMQ Queues", ReadOnly = true)]
    [Description("List RabbitMQ queues through the configured management API.")]
    public async Task<string> ListRabbitMqQueues(
        [Description("RabbitMQ virtual host. All-vhost listing is not allowed.")] string? virtualHost = null,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("rabbitmq", "rabbitmq_list_queues", "list", virtualHost, "shared", () => hub.ListRabbitMqQueuesAsync(virtualHost, cancellationToken), cancellationToken);

    [McpServerTool(Name = "rabbitmq_get_queue", Title = "Get RabbitMQ Queue", ReadOnly = true)]
    [Description("Get details for a RabbitMQ queue through the configured management API.")]
    public async Task<string> GetRabbitMqQueue(
        [Description("RabbitMQ virtual host.")] string virtualHost,
        [Description("Queue name.")] string queue,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("rabbitmq", "rabbitmq_get_queue", "get", $"{virtualHost}/{queue}", "shared", () => hub.GetRabbitMqQueueAsync(virtualHost, queue, cancellationToken), cancellationToken);

    [McpServerTool(Name = "rabbitmq_peek_queue", Title = "Peek RabbitMQ Queue", ReadOnly = true)]
    [Description("Non-destructively peek messages from a RabbitMQ queue. Messages are requeued; count and payload size are capped.")]
    public async Task<string> PeekRabbitMqQueue(
        [Description("RabbitMQ virtual host.")] string virtualHost,
        [Description("Queue name.")] string queue,
        [Description("Number of messages to peek. Defaults to 5, capped at 20.")] int? count = null,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("rabbitmq", "rabbitmq_peek_queue", "peek", $"{virtualHost}/{queue}", "shared", () => hub.PeekRabbitMqQueueAsync(virtualHost, queue, count, cancellationToken), cancellationToken);

    [McpServerTool(Name = "mysql_list_schemas", Title = "List MySQL Schemas", ReadOnly = true)]
    [Description("List indexed database schema projects through the MCP Hub MySQL provider.")]
    public async Task<string> ListMySqlSchemas(
        [Description("Optional search across schema project, server, and database names.")] string? search = null,
        [Description("Optional server name filter.")] string? server = null,
        [Description("Optional database name filter.")] string? database = null,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size.")] int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("mysql", "mysql_list_schemas", "list", "indexed-schemas", "none", () => hub.ListSchemasAsync(search, server, database, page, pageSize, cancellationToken), cancellationToken);

    [McpServerTool(Name = "mysql_get_schema_catalog", Title = "Get MySQL Schema Catalog", ReadOnly = true)]
    [Description("Get a detailed catalog for an indexed database schema through the MCP Hub MySQL provider.")]
    public async Task<string> GetMySqlSchemaCatalog(
        [Description("Schema project name from mysql_list_schemas, for example db:server/database.")] string name,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("mysql", "mysql_get_schema_catalog", "get", name, "none", () => hub.GetSchemaCatalogAsync(name, cancellationToken), cancellationToken);

    [McpServerTool(Name = "mysql_readonly_query", Title = "Run MySQL Read-only Query", ReadOnly = true)]
    [Description("Run a bounded guarded read-only SQL statement against a configured MySQL source.")]
    public async Task<string> RunMySqlReadOnlyQuery(
        [Description("Credential source name. Use default when unsure.")] string source,
        [Description("A single SELECT, SHOW, DESCRIBE, DESC, or EXPLAIN statement.")] string sql,
        [Description("Maximum rows for SELECT results. Defaults to 100 and caps at 500.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        await InvokeAuditedAsync("mysql", "mysql_readonly_query", "query", source, "shared", () => hub.RunReadOnlySqlAsync(source, sql, limit, cancellationToken), cancellationToken);

    private Task<string> ShortcutGet(
        string toolName,
        string path,
        string operation,
        string? resourceKey,
        CancellationToken ct,
        params (string Name, object? Value)[] query) =>
        InvokeAuditedAsync("shortcut", toolName, operation, resourceKey, "delegated",
            () => hub.InvokeShortcutApiAsync(
                Username,
                HttpMethod.Get,
                path,
                bodyJson: null,
                query: McpHubService.BuildQuery(query),
                ct),
            ct);

    private Task<string> ShortcutSend(
        string toolName,
        HttpMethod method,
        string path,
        string? bodyJson,
        string operation,
        string? resourceKey,
        CancellationToken ct) =>
        InvokeAuditedAsync("shortcut", toolName, operation, resourceKey, "delegated",
            () => hub.InvokeShortcutApiAsync(Username, method, path, bodyJson, ct: ct),
            ct);

    private Task<string> ChangeCurrentUserAssignment(
        int storyPublicId,
        bool assign,
        string toolName,
        CancellationToken ct) =>
        InvokeAuditedAsync("shortcut", toolName, assign ? "assign" : "unassign", $"story:{storyPublicId}", "delegated", async () =>
        {
            var storyJson = await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, $"stories/{storyPublicId}", ct: ct);
            using var storyDoc = JsonDocument.Parse(storyJson);
            var owners = GetStringArray(storyDoc.RootElement, "owner_ids").ToList();

            var memberJson = await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, "member", ct: ct);
            using var memberDoc = JsonDocument.Parse(memberJson);
            var userId = TryGetString(memberDoc.RootElement, "id")
                ?? throw new McpHubProviderPolicyException("Shortcut current member response did not include an id.");

            var changed = false;
            if (assign)
            {
                if (!owners.Contains(userId, StringComparer.Ordinal))
                {
                    owners.Add(userId);
                    changed = true;
                }
            }
            else
            {
                changed = owners.RemoveAll(item => item == userId) > 0;
            }
            if (!changed)
                return JsonSerializer.Serialize(new { message = assign ? "Current user is already an owner." : "Current user was not an owner." });

            return await hub.InvokeShortcutApiAsync(
                Username,
                HttpMethod.Put,
                $"stories/{storyPublicId}",
                McpHubService.JsonBody(("owner_ids", owners)),
                ct: ct);
        }, ct);

    private Task<string> ChangeStoryExternalLinks(
        int storyPublicId,
        string toolName,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> update,
        CancellationToken ct) =>
        InvokeAuditedAsync("shortcut", toolName, "external-links", $"story:{storyPublicId}", "delegated", async () =>
        {
            var storyJson = await hub.InvokeShortcutApiAsync(Username, HttpMethod.Get, $"stories/{storyPublicId}", ct: ct);
            using var storyDoc = JsonDocument.Parse(storyJson);
            var links = update(GetStringArray(storyDoc.RootElement, "external_links"));
            return await hub.InvokeShortcutApiAsync(
                Username,
                HttpMethod.Put,
                $"stories/{storyPublicId}",
                McpHubService.JsonBody(("external_links", links)),
                ct: ct);
        }, ct);

    private static string MergeJson(string bodyJson, params (string Name, object? Value)[] values)
    {
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(bodyJson)
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            body[name] = value;
        }

        return JsonSerializer.Serialize(body);
    }

    private static object? JsonArrayOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new McpHubProviderPolicyException("Expected a JSON array.");
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<string> JsonStringArray(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new McpHubProviderPolicyException("Expected a JSON string array.");
        return doc.RootElement.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return [];
        return property.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? TryGetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static string Slugify(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^\w-]+", "-").Trim('-');

    private static string BuildShortcutSearch(string statusFilter, string? team) =>
        string.IsNullOrWhiteSpace(team)
            ? statusFilter
            : $"{statusFilter} team:\"{team.Trim().Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private async Task<string> InvokeAuditedAsync(
        string providerKey,
        string toolName,
        string operation,
        string? resourceKey,
        string credentialMode,
        Func<Task<string>> action,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await action();
            await hub.AuditAsync(
                Username,
                TokenId,
                providerKey,
                toolName,
                "invoke",
                operation,
                resourceKey,
                credentialMode,
                "allowed",
                "ok",
                (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds),
                true,
                null,
                ct);
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var statusClass = ex is McpHubProviderPolicyException ? "policy_denied" : "provider_error";
            await hub.AuditAsync(
                Username,
                TokenId,
                providerKey,
                toolName,
                "invoke",
                operation,
                resourceKey,
                credentialMode,
                statusClass == "policy_denied" ? "denied" : "allowed",
                statusClass,
                (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds),
                false,
                ex.Message,
                ct);
            return $"Provider call failed: {ex.Message}";
        }
    }

    private string? Username =>
        httpContextAccessor.HttpContext?.User.FindFirstValue("preferred_username") ??
        httpContextAccessor.HttpContext?.User.FindFirstValue("username") ??
        httpContextAccessor.HttpContext?.User.Identity?.Name;

    private long? TokenId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.User.FindFirstValue("mcp_pat_token_id");
            return long.TryParse(value, out var parsed) ? parsed : null;
        }
    }
}

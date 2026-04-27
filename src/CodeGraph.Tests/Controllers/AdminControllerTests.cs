using System.Security.Claims;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Prompts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.Controllers;

public class AdminControllerTests
{
    [Fact]
    public async Task ListAdmins_ReturnsStoredAdminUsers()
    {
        var store = new RecordingAdminStore();
        await store.AddAdminUserAsync(new AdminUserEntity
        {
            Username = "michael",
            CreatedAt = new DateTime(2026, 4, 26, 12, 0, 0, DateTimeKind.Utc)
        });
        var controller = CreateController(store);

        var result = await controller.ListAdmins();

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var admins = ok.Value.ShouldBeAssignableTo<IReadOnlyList<AdminUserResponse>>();
        admins.ShouldNotBeNull();
        admins.Single().Username.ShouldBe("michael");
    }

    [Fact]
    public async Task AddAdmin_NormalizesAndPersistsUsername()
    {
        var store = new RecordingAdminStore();
        var controller = CreateController(store);

        var result = await controller.AddAdmin(new AddAdminRequest(" Michael "));

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var admin = ok.Value.ShouldBeOfType<AdminUserResponse>();
        admin.Username.ShouldBe("michael");
        (await store.IsAdminAsync("michael")).ShouldBeTrue();
    }

    [Fact]
    public async Task AddAdmin_ReturnsConflict_WhenUsernameAlreadyExists()
    {
        var store = new RecordingAdminStore();
        await store.AddAdminUserAsync(new AdminUserEntity { Username = "michael", CreatedAt = DateTime.UtcNow });
        var controller = CreateController(store);

        var result = await controller.AddAdmin(new AddAdminRequest("MICHAEL"));

        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task RemoveAdmin_RemovesNormalizedUsername()
    {
        var store = new RecordingAdminStore();
        await store.AddAdminUserAsync(new AdminUserEntity { Username = "michael", CreatedAt = DateTime.UtcNow });
        var controller = CreateController(store);

        var result = await controller.RemoveAdmin(" Michael ");

        result.ShouldBeOfType<NoContentResult>();
        (await store.IsAdminAsync("michael")).ShouldBeFalse();
    }

    [Fact]
    public async Task ListPrompts_ReturnsGroupedPromptCatalog()
    {
        var controller = CreateController(new RecordingAdminStore());

        var result = await controller.ListPrompts();

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var groups = ok.Value.ShouldBeAssignableTo<IReadOnlyList<AgentPromptGroupResponse>>();
        groups.ShouldNotBeNull();
        groups.Any(g => g.Category == "code-review").ShouldBeTrue();
        groups.Any(g => g.Category == "ask-assistant").ShouldBeTrue();
    }

    [Fact]
    public async Task UpdatePrompt_PersistsOverrideWithAuthenticatedUsername()
    {
        var store = new RecordingAdminStore();
        var controller = CreateController(store, "Michael");

        var result = await controller.UpdatePrompt(
            AgentPromptCatalog.GraphAssistantSystemPromptKey,
            new UpdateAgentPromptRequest { PromptText = " Custom graph assistant prompt " });

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var prompt = ok.Value.ShouldBeOfType<AgentPromptResponse>();
        prompt.EffectiveText.ShouldBe("Custom graph assistant prompt");
        prompt.HasOverride.ShouldBeTrue();
        prompt.UpdatedBy.ShouldBe("Michael");
        (await store.GetPromptOverrideAsync(AgentPromptCatalog.GraphAssistantSystemPromptKey))!.PromptText
            .ShouldBe("Custom graph assistant prompt");
    }

    [Fact]
    public async Task UpdatePrompt_ReturnsNotFound_ForUnknownPrompt()
    {
        var controller = CreateController(new RecordingAdminStore(), "Michael");

        var result = await controller.UpdatePrompt(
            "unknown.prompt",
            new UpdateAgentPromptRequest { PromptText = "hello" });

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ResetPrompt_RemovesOverride()
    {
        var store = new RecordingAdminStore();
        await store.UpsertPromptOverrideAsync(new AgentPromptOverrideEntity
        {
            PromptKey = AgentPromptCatalog.RepositoryAnalysisSystemPromptKey,
            PromptText = "custom",
            UpdatedBy = "michael",
            UpdatedAt = DateTime.UtcNow
        });
        var controller = CreateController(store);

        var result = await controller.ResetPrompt(AgentPromptCatalog.RepositoryAnalysisSystemPromptKey);

        result.ShouldBeOfType<NoContentResult>();
        (await store.GetPromptOverrideAsync(AgentPromptCatalog.RepositoryAnalysisSystemPromptKey)).ShouldBeNull();
    }

    private static AdminController CreateController(RecordingAdminStore store, string? username = null)
    {
        var claims = string.IsNullOrWhiteSpace(username)
            ? Array.Empty<Claim>()
            : [new Claim("preferred_username", username)];

        return new AdminController(store, new AgentPromptService(store))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }

    private sealed class RecordingAdminStore : IAdminStore
    {
        private readonly List<AdminUserEntity> _admins = [];
        private readonly List<AgentPromptOverrideEntity> _promptOverrides = [];

        public Task<IReadOnlyList<AdminUserEntity>> ListAdminUsersAsync() =>
            Task.FromResult<IReadOnlyList<AdminUserEntity>>(_admins.OrderBy(a => a.Username).ToList());

        public Task<bool> IsAdminAsync(string username) =>
            Task.FromResult(_admins.Any(a => a.Username == username));

        public Task<AdminUserEntity> AddAdminUserAsync(AdminUserEntity entity)
        {
            entity.Id = _admins.Count + 1;
            _admins.Add(entity);
            return Task.FromResult(entity);
        }

        public Task<bool> RemoveAdminUserAsync(string username)
        {
            var removed = _admins.RemoveAll(a => a.Username == username) > 0;
            return Task.FromResult(removed);
        }

        public Task<SettingsOverrideEntity?> GetLatestSettingsOverrideAsync() =>
            Task.FromResult<SettingsOverrideEntity?>(null);

        public Task UpsertSettingsOverrideAsync(SettingsOverrideEntity entity) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AgentPromptOverrideEntity>> ListPromptOverridesAsync() =>
            Task.FromResult<IReadOnlyList<AgentPromptOverrideEntity>>(_promptOverrides.Select(Clone).ToList());

        public Task<AgentPromptOverrideEntity?> GetPromptOverrideAsync(string promptKey) =>
            Task.FromResult(_promptOverrides
                .Where(p => string.Equals(p.PromptKey, promptKey, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .FirstOrDefault());

        public Task UpsertPromptOverrideAsync(AgentPromptOverrideEntity entity)
        {
            var existing = _promptOverrides.FirstOrDefault(p =>
                string.Equals(p.PromptKey, entity.PromptKey, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                _promptOverrides.Add(Clone(entity));
            }
            else
            {
                existing.PromptText = entity.PromptText;
                existing.UpdatedBy = entity.UpdatedBy;
                existing.UpdatedAt = entity.UpdatedAt;
            }
            return Task.CompletedTask;
        }

        public Task<bool> DeletePromptOverrideAsync(string promptKey)
        {
            var existing = _promptOverrides.FirstOrDefault(p =>
                string.Equals(p.PromptKey, promptKey, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                return Task.FromResult(false);
            }

            _promptOverrides.Remove(existing);
            return Task.FromResult(true);
        }

        private static AgentPromptOverrideEntity Clone(AgentPromptOverrideEntity entity) => new()
        {
            Id = entity.Id,
            PromptKey = entity.PromptKey,
            PromptText = entity.PromptText,
            UpdatedBy = entity.UpdatedBy,
            UpdatedAt = entity.UpdatedAt
        };
    }
}

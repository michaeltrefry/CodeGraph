using System.Security.Claims;
using CodeGraph.Api.Auth;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Memory;

public class MemoryTenantIsolationTests
{
    [Fact]
    public void AuthenticatedIdentities_AreCanonicalAndLegacyDefaultRemainsQuarantined()
    {
        MemoryTenantContext.ForAuthenticatedUser(" Alice ").ShouldBe("user:alice");
        MemoryTenantContext.ForAuthenticatedUser("default").ShouldBe("user:default");
        MemoryTenantContext.NormalizeAdministrativeTarget("default").ShouldBe("default");

        var context = new MemoryTenantContext();
        context.Username.ShouldBe(MemoryTenantContext.SystemUsername);
        Should.Throw<InvalidOperationException>(() => context.Enter("default"));

        using (context.Enter("user:alice"))
        {
            context.Username.ShouldBe("user:alice");
            using (context.Enter("default", allowLegacyDefault: true))
                context.Username.ShouldBe("default");
            context.Username.ShouldBe("user:alice");
        }

        context.Username.ShouldBe(MemoryTenantContext.SystemUsername);
    }

    [Fact]
    public async Task AdministrativeMemoryOperations_RequireAdminPolicyAndWriteRequestAndOutcomeAudit()
    {
        var authorization = typeof(AdminMemoryController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        authorization.Policy.ShouldBe(CodeGraphAuthenticationDefaults.AdminPolicy);

        var operations = new FakeMemoryAdministrationService();
        var auditStore = new RecordingMemoryAdminAuditStore();
        var controller = CreateAdminController(operations, auditStore);

        var response = await controller.GetDiagnostics("Alice", 5, 3, CancellationToken.None);

        response.Result.ShouldBeOfType<OkObjectResult>();
        operations.TargetUsername.ShouldBe("Alice");
        auditStore.Entries.ShouldHaveSingleItem();
        auditStore.Entries[0].ActorUsername.ShouldBe("user:adminuser");
        auditStore.Entries[0].TargetUsername.ShouldBe("user:alice");
        auditStore.Entries[0].Operation.ShouldBe("diagnostics");
        auditStore.Entries[0].OutcomeStatus.ShouldBe("completed");
        auditStore.Entries[0].Succeeded.ShouldBe(true);
        auditStore.Entries[0].CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task AdministrativeMemoryOperations_FailClosedWhenRequestAuditCannotBePersisted()
    {
        var operations = new FakeMemoryAdministrationService();
        var auditStore = new RecordingMemoryAdminAuditStore { FailCreate = true };
        var controller = CreateAdminController(operations, auditStore);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            controller.GetDiagnostics("Alice", 5, 3, CancellationToken.None));

        operations.CallCount.ShouldBe(0);
        auditStore.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task AdministrativeMemoryOperations_RecordActionFailureWithoutReplacingOriginalException()
    {
        var operations = new FakeMemoryAdministrationService { Failure = new ArgumentException("action failed") };
        var auditStore = new RecordingMemoryAdminAuditStore();
        var controller = CreateAdminController(operations, auditStore);

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            controller.GetDiagnostics("Alice", 5, 3, CancellationToken.None));

        exception.Message.ShouldBe("action failed");
        auditStore.Entries.ShouldHaveSingleItem();
        auditStore.Entries[0].OutcomeStatus.ShouldBe("failed");
        auditStore.Entries[0].Succeeded.ShouldBe(false);
        auditStore.Entries[0].ErrorType.ShouldBe(nameof(ArgumentException));
    }

    [Fact]
    public async Task AdministrativeMemoryOperations_ReturnSuccessfulActionWhenCompletionAuditFails()
    {
        var operations = new FakeMemoryAdministrationService();
        var auditStore = new RecordingMemoryAdminAuditStore { FailSetOutcome = true };
        var controller = CreateAdminController(operations, auditStore);

        var response = await controller.GetDiagnostics("Alice", 5, 3, CancellationToken.None);

        response.Result.ShouldBeOfType<OkObjectResult>();
        operations.CallCount.ShouldBe(1);
        auditStore.Entries.ShouldHaveSingleItem();
        auditStore.Entries[0].OutcomeStatus.ShouldBe("pending");
        auditStore.Entries[0].Succeeded.ShouldBeNull();
        auditStore.Entries[0].CorrelationId.ShouldNotBeNullOrWhiteSpace();
    }

    private static AdminMemoryController CreateAdminController(
        IMemoryAdministrationService operations,
        IMemoryAdminAuditStore auditStore) => new(
            operations,
            auditStore,
            NullLogger<AdminMemoryController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("preferred_username", "AdminUser")
                    ], "test"))
                }
            }
        };

    private sealed class RecordingMemoryAdminAuditStore : IMemoryAdminAuditStore
    {
        public List<MemoryAdminAuditEntity> Entries { get; } = [];
        public bool FailCreate { get; init; }
        public bool FailSetOutcome { get; init; }

        public Task<long> CreatePendingAsync(MemoryAdminAuditEntity audit, CancellationToken ct = default)
        {
            if (FailCreate)
                throw new InvalidOperationException("audit unavailable");

            audit.Id = Entries.Count + 1;
            Entries.Add(audit);
            return Task.FromResult(audit.Id);
        }

        public Task SetOutcomeAsync(
            long auditId,
            string outcomeStatus,
            bool succeeded,
            string? errorType,
            CancellationToken ct = default)
        {
            if (FailSetOutcome)
                throw new InvalidOperationException("audit unavailable");

            var audit = Entries.Single(entry => entry.Id == auditId);
            audit.OutcomeStatus = outcomeStatus;
            audit.Succeeded = succeeded;
            audit.ErrorType = errorType;
            audit.CompletedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMemoryAdministrationService : IMemoryAdministrationService
    {
        public string? TargetUsername { get; private set; }
        public int CallCount { get; private set; }
        public Exception? Failure { get; init; }

        public Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
            string targetUsername,
            int staleAfterMinutes,
            int sampleLimit,
            CancellationToken ct = default)
        {
            CallCount++;
            TargetUsername = targetUsername;
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(new MemoryWriteDiagnosticsResult());
        }

        public Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
            string targetUsername,
            int staleAfterMinutes,
            int sampleLimit,
            CancellationToken ct = default)
        {
            CallCount++;
            TargetUsername = targetUsername;
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(new MemoryDiagnosticsResult());
        }

        public Task<MemoryCleanupResult> DeleteMemoryBySourceAsync(
            string targetUsername,
            string source,
            bool dryRun,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MemoryCleanupResult> DeleteMemoryTestDataAsync(
            string targetUsername,
            bool dryRun,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MemoryCleanupResult> DeleteMemoryByIdsAsync(
            string targetUsername,
            IReadOnlyList<string> claimIds,
            IReadOnlyList<string> entityIds,
            bool dryRun,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}

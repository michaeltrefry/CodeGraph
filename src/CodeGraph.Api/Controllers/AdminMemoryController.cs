using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
[Route("api/admin/memory")]
[Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
public sealed class AdminMemoryController(
    IMemoryAdministrationService memoryAdministration,
    IMemoryAdminAuditStore auditStore,
    ILogger<AdminMemoryController> logger) : ControllerBase
{
    [HttpGet("{targetUsername}/writes/diagnostics")]
    public async Task<ActionResult<MemoryWriteDiagnosticsResult>> GetWriteDiagnostics(
        string targetUsername,
        [FromQuery] int? staleAfterMinutes,
        [FromQuery] int? sampleLimit,
        CancellationToken ct)
        => Ok(await ExecuteAuditedAsync(
            "writes.diagnostics",
            targetUsername,
            dryRun: true,
            () => memoryAdministration.GetWriteDiagnosticsAsync(
                targetUsername,
                Math.Clamp(staleAfterMinutes ?? 15, 1, 1440),
                Math.Clamp(sampleLimit ?? 10, 1, 100),
                ct)));

    [HttpGet("{targetUsername}/diagnostics")]
    public async Task<ActionResult<MemoryDiagnosticsResult>> GetDiagnostics(
        string targetUsername,
        [FromQuery] int? staleAfterMinutes,
        [FromQuery] int? sampleLimit,
        CancellationToken ct)
        => Ok(await ExecuteAuditedAsync(
            "diagnostics",
            targetUsername,
            dryRun: true,
            () => memoryAdministration.GetDiagnosticsAsync(
                targetUsername,
                Math.Clamp(staleAfterMinutes ?? 15, 1, 1440),
                Math.Clamp(sampleLimit ?? 10, 1, 100),
                ct)));

    [HttpPost("{targetUsername}/cleanup/by-source")]
    public async Task<ActionResult<MemoryCleanupResult>> DeleteBySource(
        string targetUsername,
        [FromBody] MemoryCleanupBySourceRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Source))
            return BadRequest(new { error = "Source is required" });

        return Ok(await ExecuteAuditedAsync(
            "cleanup.by_source",
            targetUsername,
            request.DryRun,
            () => memoryAdministration.DeleteMemoryBySourceAsync(
                targetUsername,
                request.Source,
                request.DryRun,
                ct)));
    }

    [HttpPost("{targetUsername}/cleanup/test-data")]
    public async Task<ActionResult<MemoryCleanupResult>> DeleteTestData(
        string targetUsername,
        [FromBody] MemoryCleanupTestDataRequest request,
        CancellationToken ct)
        => Ok(await ExecuteAuditedAsync(
            "cleanup.test_data",
            targetUsername,
            request.DryRun,
            () => memoryAdministration.DeleteMemoryTestDataAsync(targetUsername, request.DryRun, ct)));

    [HttpPost("{targetUsername}/cleanup/by-ids")]
    public async Task<ActionResult<MemoryCleanupResult>> DeleteByIds(
        string targetUsername,
        [FromBody] MemoryCleanupByIdsRequest request,
        CancellationToken ct)
    {
        if (request.ClaimIds.Count == 0 && request.EntityIds.Count == 0)
            return BadRequest(new { error = "At least one claim id or entity id is required" });

        return Ok(await ExecuteAuditedAsync(
            "cleanup.by_ids",
            targetUsername,
            request.DryRun,
            () => memoryAdministration.DeleteMemoryByIdsAsync(
                targetUsername,
                request.ClaimIds,
                request.EntityIds,
                request.DryRun,
                ct)));
    }

    private async Task<T> ExecuteAuditedAsync<T>(
        string operation,
        string targetUsername,
        bool dryRun,
        Func<Task<T>> action)
    {
        var actor = MemoryTenantContext.ForAuthenticatedUser(User.GetUsername());
        var target = MemoryTenantContext.NormalizeAdministrativeTarget(targetUsername);

        var audit = CreateAudit(actor, target, operation, dryRun);
        var auditId = await auditStore.CreatePendingAsync(audit, CancellationToken.None);

        T result;
        try
        {
            result = await action();
        }
        catch (Exception ex)
        {
            try
            {
                await auditStore.SetOutcomeAsync(
                    auditId,
                    "failed",
                    succeeded: false,
                    ex.GetType().Name,
                    CancellationToken.None);
            }
            catch (Exception auditException)
            {
                // The durable pending record is deliberately left untouched. It identifies an
                // attempted operation whose final outcome needs administrative reconciliation.
                logger.LogCritical(
                    auditException,
                    "Failed to persist failed outcome for memory admin audit {AuditCorrelationId}",
                    audit.CorrelationId);
            }

            throw;
        }

        try
        {
            await auditStore.SetOutcomeAsync(
                auditId,
                "completed",
                succeeded: true,
                errorType: null,
                CancellationToken.None);
        }
        catch (Exception auditException)
        {
            // The action has already succeeded. Returning an error would invite a destructive
            // retry and would falsely report the action itself as failed. The pending audit row
            // remains a durable, correlatable unknown outcome for reconciliation.
            logger.LogCritical(
                auditException,
                "Memory admin operation succeeded but its audit outcome remains pending for {AuditCorrelationId}",
                audit.CorrelationId);
        }

        return result;
    }

    private static MemoryAdminAuditEntity CreateAudit(
        string actor,
        string target,
        string operation,
        bool dryRun) => new()
    {
        ActorUsername = actor,
        TargetUsername = target,
        Operation = operation,
        DryRun = dryRun,
        OutcomeStatus = "pending",
        Succeeded = null,
    };
}

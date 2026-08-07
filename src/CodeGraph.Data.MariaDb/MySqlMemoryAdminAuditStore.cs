using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlMemoryAdminAuditStore(CodeGraphDbContext db) : IMemoryAdminAuditStore
{
    public async Task<long> CreatePendingAsync(MemoryAdminAuditEntity audit, CancellationToken ct = default)
    {
        db.MemoryAdminAudit.Add(audit);
        await db.SaveChangesAsync(ct);
        db.Entry(audit).State = EntityState.Detached;
        return audit.Id;
    }

    public async Task SetOutcomeAsync(
        long auditId,
        string outcomeStatus,
        bool succeeded,
        string? errorType,
        CancellationToken ct = default)
    {
        var updated = await db.MemoryAdminAudit
            .Where(audit => audit.Id == auditId && audit.OutcomeStatus == "pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(audit => audit.OutcomeStatus, outcomeStatus)
                .SetProperty(audit => audit.Succeeded, succeeded)
                .SetProperty(audit => audit.ErrorType, errorType)
                .SetProperty(audit => audit.CompletedAt, DateTime.UtcNow), ct);
        if (updated != 1)
            throw new InvalidOperationException($"Pending memory admin audit {auditId} was not found.");
    }
}

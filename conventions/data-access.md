# Data Access Conventions

All services use **MySQL** with a hybrid ORM approach.

## Hybrid Pattern

- **EF Core** (via Pomelo MySQL provider) — CRUD operations, migrations, entity tracking
- **Dapper** — Complex queries, recursive CTEs, batch operations, performance-critical reads

Both coexist in the same Data project, often in the same repository class.

## Repository Pattern

```csharp
// In TC.OrdersApi.Data
public class OrderRepository : IOrderRepository
{
    private readonly AppDbContext _context;      // EF Core
    private readonly IDbConnection _connection;  // Dapper

    public async Task<Order> GetByIdAsync(int id)
        => await _context.Orders.FindAsync(id);  // EF Core for simple lookups

    public async Task<IEnumerable<OrderSummary>> GetDashboardAsync(int accountId)
        => await _connection.QueryAsync<OrderSummary>(           // Dapper for complex queries
            "SELECT ... FROM orders o JOIN ... WHERE o.account_id = @AccountId",
            new { AccountId = accountId });
}
```

## MySQL-Specific SQL

- `ON DUPLICATE KEY UPDATE` (not `ON CONFLICT`)
- `JSON_MERGE_PATCH()` (not `json_patch()`)
- `LIKE CONCAT('%', ?)` (not `LIKE '%' || ?`)
- `BIGINT AUTO_INCREMENT PRIMARY KEY` (not `INTEGER PRIMARY KEY AUTOINCREMENT`)

## Connection Management

- EF Core `DbContext` is scoped per request via DI
- Dapper connections are typically resolved from DI or created from a connection string
- No manual transaction management in most cases — EF Core handles it

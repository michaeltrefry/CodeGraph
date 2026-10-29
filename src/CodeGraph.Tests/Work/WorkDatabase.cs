using System.Text.Json;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using CodeGraph.Tests.Data;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// A fresh MariaDB database with every migration applied, shared by the tests of one class, and the
/// work tools over it exactly as MCP and REST run them (raw JSON arguments into WorkService).
/// Tests use their own project keys, so they do not see each other's work.
/// </summary>
public sealed class WorkDatabase : IAsyncLifetime
{
    public static readonly string[] Repositories = ["sceneworks", "inference", "mlx-rs", "codegraph"];

    public string ConnectionString { get; private set; } = "";
    public MySqlWorkStore Store { get; private set; } = null!;
    public WorkService Service { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString())
        {
            Database = $"cg_work_{Guid.NewGuid():N}",
            // The concurrency tests open a connection per concurrent caller.
            MaximumPoolSize = 200
        };
        ConnectionString = builder.ConnectionString;
        var options = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        await new MariaDbMigrationRunner(options, NullLogger<MariaDbMigrationRunner>.Instance).ApplyConfiguredMigrationsAsync();

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.ExecuteAsync("INSERT INTO repositories (name) VALUES (@name)", Repositories.Select(name => new { name }));

        Store = new MySqlWorkStore(options);
        Service = new WorkService(Store);
    }

    public async Task DisposeAsync()
    {
        var builder = new MySqlConnectionStringBuilder(ConnectionString);
        var database = builder.Database;
        builder.Database = "";
        MySqlConnection.ClearAllPools();
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{database}`");
    }

    /// <summary>Runs a tool as <paramref name="user"/> and answers its result as wire JSON.</summary>
    public async Task<JsonElement> CallAsync(string tool, object arguments, string user = "alice")
    {
        var result = await Service.ExecuteAsync(tool, JsonSerializer.SerializeToElement(arguments, WorkJson.Options), new WorkActor(user));
        return JsonSerializer.SerializeToElement(result, result.GetType(), WorkJson.Options);
    }

    public async Task<WorkException> RefusedAsync(string tool, object arguments, string user = "alice") =>
        await Should.ThrowAsync<WorkException>(() => CallAsync(tool, arguments, user));

    /// <summary>A project with a fresh unique key, owning <paramref name="owned"/>.</summary>
    public async Task<string> NewProjectAsync(string prefix, params string[] owned)
    {
        var key = (prefix + Guid.NewGuid().ToString("N")[..6]).ToUpperInvariant()[..Math.Min(10, prefix.Length + 6)];
        await CallAsync(WorkTools.CreateWork, new
        {
            new_project = new
            {
                key,
                name = $"Project {key}",
                repositories = owned.Select(repository => new { repository, role = "owned" }).ToArray()
            }
        });
        return key;
    }

    public async Task<JsonElement> NewItemAsync(string project, string title, string status = "ready", string[]? blockedBy = null, string priority = "medium")
    {
        var created = await CallAsync(WorkTools.CreateWork, new { project, item = new { title, status, priority, blocked_by = blockedBy } });
        return created.GetProperty("item");
    }

    public async Task<JsonElement> UpdateAsync(string id, object fields, string user = "alice")
    {
        var current = await CallAsync(WorkTools.GetWork, new { id });
        var arguments = JsonSerializer.SerializeToNode(fields, WorkJson.Options)!.AsObject();
        arguments["id"] = id;
        arguments["expected_version"] = current.GetProperty("version").GetInt64();
        var updated = await CallAsync(WorkTools.UpdateWork, arguments, user);
        return updated.EnumerateObject().Single().Value;
    }

    /// <summary>Every change-feed entry of a project, oldest first.</summary>
    public async Task<List<JsonElement>> AllChangesAsync(string project)
    {
        var entries = new List<JsonElement>();
        var page = await CallAsync(WorkTools.Changes, new { project, from_start = true, limit = 200 });
        while (true)
        {
            var items = page.GetProperty("items").EnumerateArray().ToList();
            entries.AddRange(items);
            if (items.Count < 200)
                return entries;
            page = await CallAsync(WorkTools.Changes, new { project, cursor = page.GetProperty("next_cursor").GetString(), limit = 200 });
        }
    }
}

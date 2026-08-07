using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Data;

[Collection(nameof(HostEntrypointTopologyCollection))]
public class MariaDbHostEntrypointTopologyTests
{
    private static readonly (string Name, string Project, int? Port)[] Hosts =
    [
        ("api", "CodeGraph.Api", 5037),
        ("indexer", "CodeGraph.Indexer.Host", 5042),
        ("memory", "CodeGraph.Memory.Host", 5039),
        ("metrics", "CodeGraph.Metrics", 5041),
        ("jobs", "CodeGraph.Jobs", null)
    ];

    [Fact]
    public async Task ActualHostEntrypoints_BlockReadinessAndWorkersUntilMigrationCompletes()
    {
        var baseConnectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(baseConnectionString);
        var databaseName = $"cg_host_topology_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var migrationsPath = Path.Combine(Path.GetTempPath(), $"cg-host-topology-{Guid.NewGuid():N}");
        Directory.CreateDirectory(migrationsPath);
        await File.WriteAllTextAsync(
            Path.Combine(migrationsPath, "999_host_topology_probe.sql"),
            "CREATE TABLE IF NOT EXISTS host_topology_probe (id INT NOT NULL PRIMARY KEY);");
        var processes = new List<HostProcess>();
        MySqlConnection? lockConnection = null;

        try
        {
            foreach (var (_, _, port) in Hosts.Where(host => host.Port is not null))
            {
                (await CanConnectAsync(port!.Value)).ShouldBeFalse(
                    $"port {port} must be free before the topology fixture starts");
            }

            var fullMigrationsPath = Path.Combine(repoRoot, "sql/migrations");
            var setupRunner = CreateRunner(builder.ConnectionString, fullMigrationsPath);
            await setupRunner.ApplyConfiguredMigrationsAsync();

            await using (var setupConnection = new MySqlConnection(builder.ConnectionString))
            {
                await setupConnection.OpenAsync();
                await setupConnection.ExecuteAsync("""
                    INSERT INTO job_schedules (
                        name, job_type, is_enabled, cron_expression, time_zone_id,
                        args_json, next_run_utc)
                    VALUES (
                        'host-topology-probe', 'topology-probe', TRUE, '* * * * *', 'UTC',
                        '{}', UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE)
                    ON DUPLICATE KEY UPDATE next_run_utc = VALUES(next_run_utc);
                    """);
            }

            lockConnection = new MySqlConnection(builder.ConnectionString);
            await lockConnection.OpenAsync();
            var lockName = MariaDbMigrationRunner.BuildLockName(databaseName);
            (await lockConnection.ExecuteScalarAsync<int>(
                "SELECT GET_LOCK(@LockName, 0)",
                new { LockName = lockName })).ShouldBe(1);

            foreach (var host in Hosts)
            {
                processes.Add(StartHost(host, repoRoot, builder.ConnectionString, migrationsPath));
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
            processes.ShouldAllBe(host => !host.Process.HasExited, BuildProcessFailureMessage(processes));
            foreach (var (_, _, port) in Hosts.Where(host => host.Port is not null))
            {
                (await CanConnectAsync(port!.Value)).ShouldBeFalse(
                    $"{port} accepted traffic while its migration lock was blocked");
            }

            var pendingMigrationCount = await lockConnection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*)
                FROM migration_history
                WHERE script_name = '999_host_topology_probe.sql'
                """);
            var prematureWorkerCount = await lockConnection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*)
                FROM job_schedules
                WHERE name = 'host-topology-probe'
                  AND (last_run_started_utc IS NOT NULL OR lease_owner IS NOT NULL)
                """);
            pendingMigrationCount.ShouldBe(0);
            prematureWorkerCount.ShouldBe(0);

            (await lockConnection.ExecuteScalarAsync<int>(
                "SELECT RELEASE_LOCK(@LockName)",
                new { LockName = lockName })).ShouldBe(1);

            try
            {
                await WaitUntilAsync(async () =>
                {
                    if (processes.Any(host => host.Process.HasExited))
                    {
                        throw new InvalidOperationException(BuildProcessFailureMessage(processes));
                    }

                    var portChecks = await Task.WhenAll(
                        Hosts.Where(host => host.Port is not null)
                            .Select(host => CanConnectAsync(host.Port!.Value)));
                    var workerStarted = await lockConnection.ExecuteScalarAsync<int>("""
                        SELECT COUNT(*)
                        FROM job_schedules
                        WHERE name = 'host-topology-probe'
                          AND last_run_started_utc IS NOT NULL
                        """) == 1;
                    var migrationCompleted = await lockConnection.ExecuteScalarAsync<int>("""
                        SELECT COUNT(*) FROM migration_history
                        WHERE script_name = '999_host_topology_probe.sql'
                        """) == 1;
                    if (portChecks.Any(value => value) || workerStarted)
                    {
                        migrationCompleted.ShouldBeTrue(
                            "a listener or worker became active before migration history recorded completion");
                    }

                    return migrationCompleted && portChecks.All(value => value) && workerStarted;
                }, TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException ex)
            {
                var portStates = await Task.WhenAll(
                    Hosts.Where(host => host.Port is not null)
                        .Select(async host => $"{host.Name}:{await CanConnectAsync(host.Port!.Value)}"));
                var scheduleState = await lockConnection.QuerySingleAsync("""
                    SELECT last_run_started_utc, last_run_status, lease_owner
                    FROM job_schedules
                    WHERE name = 'host-topology-probe'
                    """);
                var migrationState = await lockConnection.ExecuteScalarAsync<int>("""
                    SELECT COUNT(*) FROM migration_history
                    WHERE script_name = '999_host_topology_probe.sql'
                    """);
                var logs = string.Join(
                    Environment.NewLine,
                    processes.Select(host => $"--- {host.Name} ---{Environment.NewLine}{string.Join(Environment.NewLine, host.Output)}"));
                throw new TimeoutException(
                    $"Ports={string.Join(',', portStates)}; migration={migrationState}; " +
                    $"schedule={scheduleState}{Environment.NewLine}{logs}",
                    ex);
            }

            var completedMigrationCount = await lockConnection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*)
                FROM migration_history
                WHERE script_name = '999_host_topology_probe.sql'
                """);
            completedMigrationCount.ShouldBe(1);
        }
        finally
        {
            foreach (var host in processes)
            {
                await StopHostAsync(host.Process);
            }

            if (lockConnection is not null)
            {
                await lockConnection.DisposeAsync();
            }

            Directory.Delete(migrationsPath, recursive: true);
            await DropDatabaseAsync(baseConnectionString, databaseName);
        }
    }

    private static HostProcess StartHost(
        (string Name, string Project, int? Port) host,
        string repoRoot,
        string connectionString,
        string migrationsPath)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var assemblyPath = Path.Combine(
            repoRoot,
            "src",
            host.Project,
            "bin",
            configuration,
            "net10.0",
            $"{host.Project}.dll");
        File.Exists(assemblyPath).ShouldBeTrue($"actual host assembly not found: {assemblyPath}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"] = "false";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        startInfo.Environment["CodeGraph__StorageOptions__Provider"] = "MariaDb";
        startInfo.Environment["CodeGraph__StorageOptions__MariaDbConnectionString"] = connectionString;
        startInfo.Environment["CodeGraph__StorageOptions__MariaDbMigrationsPath"] = migrationsPath;
        startInfo.Environment["CodeGraph__StorageOptions__MariaDbMigrationLockTimeoutSeconds"] = "30";
        startInfo.Environment["CodeGraph__RepositorySource__Provider"] = "Folder";
        startInfo.Environment["CodeGraph__RepositorySource__Folder__RootPath"] = Path.GetTempPath();
        startInfo.Environment["CodeGraph__InternalServiceAuth__Enabled"] = "false";
        startInfo.Environment["CodeGraph__RabbitMqOptions__Host"] = "127.0.0.1";
        startInfo.Environment["CodeGraph__RabbitMqOptions__Username"] = "guest";
        startInfo.Environment["CodeGraph__RabbitMqOptions__Password"] = "guest";

        var output = new ConcurrentQueue<string>();
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                output.Enqueue(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                output.Enqueue(args.Data);
        };
        process.Start().ShouldBeTrue();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new HostProcess(host.Name, process, output);
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(200);
        }

        throw new TimeoutException($"Topology condition was not met within {timeout}.");
    }

    private static async Task StopHostAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        process.Dispose();
    }

    private static string BuildProcessFailureMessage(IEnumerable<HostProcess> hosts) =>
        string.Join(
            Environment.NewLine,
            hosts.Where(host => host.Process.HasExited)
                .Select(host => $"{host.Name} exited ({host.Process.ExitCode}): {string.Join(Environment.NewLine, host.Output)}"));

    private static MariaDbMigrationRunner CreateRunner(string connectionString, string migrationsPath) =>
        new(
            Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = connectionString,
                MigrationsPath = migrationsPath,
                MigrationLockTimeoutSeconds = 30
            }),
            NullLogger<MariaDbMigrationRunner>.Instance);

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Database = "" };
        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }

    private sealed record HostProcess(
        string Name,
        Process Process,
        ConcurrentQueue<string> Output);
}

[CollectionDefinition(nameof(HostEntrypointTopologyCollection), DisableParallelization = true)]
public sealed class HostEntrypointTopologyCollection;

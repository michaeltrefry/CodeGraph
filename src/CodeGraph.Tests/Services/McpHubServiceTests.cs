using System.Net;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class McpHubServiceTests
{
    [Fact]
    public async Task CatalogSeeder_DisablesExternalProviderToolsByDefault_AndOmitsWriteShortcutTool()
    {
        var store = new RecordingMcpHubStore();
        var seeder = new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore());

        await seeder.EnsureCatalogAsync();

        store.Providers.Single(provider => provider.ProviderKey == "codegraph").Enabled.ShouldBeTrue();
        store.Providers.Single(provider => provider.ProviderKey == "shortcut").Enabled.ShouldBeFalse();
        store.Tools.Single(tool => tool.ToolName == "mcp_hub_catalog").Enabled.ShouldBeTrue();
        store.Tools.Single(tool => tool.ToolName == "shortcut_search_epics").Enabled.ShouldBeFalse();
        store.Tools.ShouldNotContain(tool => tool.ToolName == "shortcut_add_story_comment");
    }

    [Fact]
    public async Task CatalogSeeder_SeedsSensitiveColumnPatterns_AndDoesNotClobberManualOverrides()
    {
        var sensitiveStore = new InMemoryMcpSensitiveColumnStore();
        // An admin has already explicitly allowed the global "token" pattern.
        await sensitiveStore.UpsertAsync(new McpSensitiveColumnEntity
        {
            SourceKey = "*",
            TableName = "*",
            ColumnName = "token",
            Allowed = true,
            IsManual = true,
        });

        await new McpHubCatalogSeeder(new RecordingMcpHubStore(), sensitiveStore).EnsureCatalogAsync();

        var rows = await sensitiveStore.ListAsync();
        rows.ShouldContain(row => row.ColumnName == "password" && !row.Allowed);
        // The pre-existing manual override survives re-seeding.
        var token = rows.Single(row => row.ColumnName == "token");
        token.Allowed.ShouldBeTrue();
        token.IsManual.ShouldBeTrue();
    }

    [Fact]
    public async Task CatalogSeeder_SeedsExplicitToolCatalogState()
    {
        var store = new RecordingMcpHubStore();

        await new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();

        // Every seeded tool exists in this deployment -> system-owned is_available is true.
        store.Tools.ShouldAllBe(tool => tool.IsAvailable);

        // Native CodeGraph tools are sensible token-creation defaults; external provider tools are not.
        store.Tools.Single(tool => tool.ToolName == "search_graph").DefaultSelected.ShouldBeTrue();
        store.Tools.Single(tool => tool.ToolName == "shortcut_search_epics").DefaultSelected.ShouldBeFalse();

        // access_class is a UI grouping label: read-only-declared tools -> "read", writes -> "write".
        store.Tools.Single(tool => tool.ToolName == "list_schemas").AccessClass.ShouldBe("read");
        store.Tools.Single(tool => tool.ToolName == "store_memory_v2").AccessClass.ShouldBe("write");
        store.Tools.ShouldAllBe(tool => tool.AccessClass == "read" || tool.AccessClass == "write");
    }

    [Fact]
    public async Task ListRabbitMqQueuesAsync_FailsClosed_WhenResourcePolicyIsMissing()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "rabbitmq", Enabled = true }]
        };
        var service = new McpHubService(
            store,
            new EmptyProjectQueryService(),
            new EmptyHttpClientFactory(),
            Policy(new InMemoryMcpSensitiveColumnStore()),
            ExposurePolicy(),
            new InMemoryMcpProviderCredentialStore(),
            NullLogger<McpHubService>.Instance);

        var ex = await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.ListRabbitMqQueuesAsync("/", CancellationToken.None));

        ex.Message.ShouldContain("not allowed by policy");
    }

    [Fact]
    public async Task PeekRabbitMqQueueAsync_FailsClosed_WhenResourcePolicyIsMissing()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "rabbitmq", Enabled = true }]
        };
        var service = new McpHubService(
            store,
            new EmptyProjectQueryService(),
            new EmptyHttpClientFactory(),
            Policy(new InMemoryMcpSensitiveColumnStore()),
            ExposurePolicy(),
            new InMemoryMcpProviderCredentialStore(),
            NullLogger<McpHubService>.Instance);

        // No allowedQueues policy configured -> denied before any RabbitMQ request.
        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.PeekRabbitMqQueueAsync("vhost", "queue", 5, CancellationToken.None));
    }

    [Fact]
    public async Task PeekRabbitMqQueueAsync_IsNonDestructive_AndCapsCountAndResponse()
    {
        HttpRequestMessage? captured = null;
        var oversizedBody = new string('x', 100 * 1024);
        var factory = new StubHttpClientFactory(
            new Uri("https://rabbit.example/"),
            request =>
            {
                captured = request;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(oversizedBody) };
            });

        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "rabbitmq", Enabled = true }]
        };
        store.Config[("rabbitmq", "allowedQueues")] = "myvhost/myqueue";
        store.Config[("rabbitmq", "managementBaseUrl")] = "https://rabbit.example";
        store.Credentials[("rabbitmq", "username")] = "guest";
        store.Credentials[("rabbitmq", "password")] = "guest";

        var service = new McpHubService(
            store,
            new EmptyProjectQueryService(),
            factory,
            Policy(new InMemoryMcpSensitiveColumnStore()),
            ExposurePolicy(),
            new InMemoryMcpProviderCredentialStore(),
            NullLogger<McpHubService>.Instance);

        // Request 50 messages — the tool must cap it.
        var result = await service.PeekRabbitMqQueueAsync("myvhost", "myqueue", 50, CancellationToken.None);

        var sentBody = await captured!.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(sentBody);
        // Non-destructive: messages are requeued, never consumed/acked away.
        doc.RootElement.GetProperty("ackmode").GetString().ShouldBe("ack_requeue_true");
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(20);
        doc.RootElement.GetProperty("truncate").GetInt32().ShouldBe(8192);
        // Oversized provider responses are truncated rather than streamed back wholesale.
        result.ShouldContain("truncated");
    }

    [Fact]
    public async Task RunReadOnlySqlAsync_FailsClosed_WhenSourceIsNotHubExposed()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "mysql", Enabled = true }]
        };
        var service = new McpHubService(
            store,
            new EmptyProjectQueryService(),
            new EmptyHttpClientFactory(),
            Policy(new InMemoryMcpSensitiveColumnStore()),
            // The source exists but mcp_hub_enabled = false -> not exposed.
            ExposurePolicy(new DatabaseSourceEntity
            {
                Id = 1,
                ServerName = "srv",
                DatabaseName = "orders",
                McpHubEnabled = false,
                McpExposureMode = McpSourceExposureModes.ReadOnlySql,
            }),
            new InMemoryMcpProviderCredentialStore(),
            NullLogger<McpHubService>.Instance);

        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.RunReadOnlySqlAsync("orders", "SELECT 1", null, CancellationToken.None));
    }

    [Fact]
    public async Task RunReadOnlySqlAsync_FailsClosed_WhenSourceIsNotReadOnlySqlMode()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "mysql", Enabled = true }]
        };
        var service = new McpHubService(
            store,
            new EmptyProjectQueryService(),
            new EmptyHttpClientFactory(),
            Policy(new InMemoryMcpSensitiveColumnStore()),
            // Exposed, but only SchemaOnly — read-only SQL is not permitted.
            ExposurePolicy(new DatabaseSourceEntity
            {
                Id = 1,
                ServerName = "srv",
                DatabaseName = "orders",
                McpHubEnabled = true,
                McpExposureMode = McpSourceExposureModes.SchemaOnly,
            }),
            new InMemoryMcpProviderCredentialStore(),
            NullLogger<McpHubService>.Instance);

        var ex = await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.RunReadOnlySqlAsync("orders", "SELECT 1", null, CancellationToken.None));
        ex.Message.ShouldContain("read-only SQL");
    }

    [Fact]
    public async Task MySqlSourceExposurePolicy_ResolvesExposedSourceByMultipleIdentifiers()
    {
        var policy = ExposurePolicy(
            new DatabaseSourceEntity
            {
                Id = 7,
                ServerName = "srv1",
                DatabaseName = "orders",
                McpDisplayName = "Orders DB",
                McpHubEnabled = true,
                McpExposureMode = McpSourceExposureModes.ReadOnlySql,
            },
            new DatabaseSourceEntity
            {
                Id = 8,
                ServerName = "srv2",
                DatabaseName = "billing",
                McpHubEnabled = false,
            });

        (await policy.ResolveExposedSourceAsync("7", CancellationToken.None)).Id.ShouldBe(7);
        (await policy.ResolveExposedSourceAsync("Orders DB", CancellationToken.None)).Id.ShouldBe(7);
        (await policy.ResolveExposedSourceAsync("srv1/orders", CancellationToken.None)).Id.ShouldBe(7);
        (await policy.ResolveExposedSourceAsync("orders", CancellationToken.None)).Id.ShouldBe(7);
        (await policy.ResolveReadOnlySqlSourceAsync("7", CancellationToken.None)).Id.ShouldBe(7);

        // The non-exposed source (id 8) is invisible to resolution.
        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            policy.ResolveExposedSourceAsync("8", CancellationToken.None));
        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            policy.ResolveExposedSourceAsync("billing", CancellationToken.None));
    }

    [Theory]
    [InlineData("SELECT LOAD_FILE('/etc/passwd')", "load_file")]
    [InlineData("select sleep(10)", "sleep")]
    [InlineData("SELECT BENCHMARK(1000000, MD5('x'))", "benchmark")]
    [InlineData("SELECT GET_LOCK('x', 10)", "get_lock")]
    public void ReadOnlySqlValidator_RejectsDangerousFunctions(string sql, string function)
    {
        var result = ReadOnlySqlValidator.Validate(sql);

        result.IsValid.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain(function);
    }

    [Theory]
    [InlineData("SELECT * FROM users INTO OUTFILE '/tmp/dump'")]
    [InlineData("SELECT * FROM users INTO DUMPFILE '/tmp/dump'")]
    [InlineData("SELECT secret FROM vault INTO @captured")]
    public void ReadOnlySqlValidator_RejectsSelectIntoFileAndVariableTargets(string sql)
    {
        ReadOnlySqlValidator.Validate(sql).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1; DROP TABLE users")]
    public void ReadOnlySqlValidator_RejectsMultipleStatements(string sql)
    {
        var result = ReadOnlySqlValidator.Validate(sql);

        result.IsValid.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("one SQL statement");
    }

    [Theory]
    [InlineData("INSERT INTO users (id) VALUES (1)")]
    [InlineData("UPDATE users SET name = 'x'")]
    [InlineData("DELETE FROM users")]
    [InlineData("DROP TABLE users")]
    [InlineData("TRUNCATE TABLE users")]
    [InlineData("ALTER TABLE users ADD COLUMN x INT")]
    [InlineData("CALL some_procedure()")]
    [InlineData("SET @x = 1")]
    public void ReadOnlySqlValidator_RejectsMutatingStatements(string sql)
    {
        ReadOnlySqlValidator.Validate(sql).IsValid.ShouldBeFalse();
    }

    [Theory]
    // A string literal that contains a blocked keyword must NOT be a false positive —
    // this is the regression the old regex blocklist could not handle.
    [InlineData("SELECT id, name FROM users WHERE status = 'create' LIMIT 5")]
    [InlineData("SELECT id FROM orders WHERE note = 'please drop off at door'")]
    [InlineData("SHOW TABLES")]
    [InlineData("DESCRIBE users")]
    [InlineData("DESC users")]
    [InlineData("EXPLAIN SELECT * FROM users")]
    [InlineData("WITH recent AS (SELECT id FROM users) SELECT id FROM recent")]
    public void ReadOnlySqlValidator_AcceptsSafeReadOnlyStatements(string sql)
    {
        ReadOnlySqlValidator.Validate(sql).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ReadOnlySqlValidator_ExposesReferencedTablesForDownstreamPolicy()
    {
        var result = ReadOnlySqlValidator.Validate(
            "SELECT u.id FROM users u JOIN orders o ON o.user_id = u.id");

        result.IsValid.ShouldBeTrue();
        result.ReferencedTables.ShouldBe(["users", "orders"], ignoreOrder: true);
    }

    [Fact]
    public void ReadOnlySqlValidator_ExposesReferencedColumns_IncludingAliasedSelections()
    {
        // The aliased projection must still surface the underlying "password" column —
        // this is the bypass the post-execution name check used to allow (sc-1051).
        var result = ReadOnlySqlValidator.Validate(
            "SELECT password AS p, u.email FROM users u WHERE id = 1");

        result.IsValid.ShouldBeTrue();
        result.HasWildcardProjection.ShouldBeFalse();
        result.ReferencedColumns.ShouldContain("password");
        result.ReferencedColumns.ShouldContain("email");
    }

    [Fact]
    public void ReadOnlySqlValidator_FlagsWildcardProjections()
    {
        ReadOnlySqlValidator.Validate("SELECT * FROM users").HasWildcardProjection.ShouldBeTrue();
        ReadOnlySqlValidator.Validate("SELECT users.* FROM users").HasWildcardProjection.ShouldBeTrue();
    }

    [Fact]
    public async Task SensitiveColumnPolicy_DeniesSensitiveColumn_BeforeExecution_IncludingAliases()
    {
        var store = new InMemoryMcpSensitiveColumnStore();
        await store.UpsertAsync(new McpSensitiveColumnEntity { ColumnName = "password" });
        var policy = Policy(store);

        var aliased = ReadOnlySqlValidator.Validate("SELECT password AS p FROM users");
        var ex = await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            policy.EnsureQueryAllowedAsync("default", aliased));
        ex.Message.ShouldContain("password");

        var direct = ReadOnlySqlValidator.Validate("SELECT password FROM users");
        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            policy.EnsureQueryAllowedAsync("default", direct));
    }

    [Fact]
    public async Task SensitiveColumnPolicy_DeniesWildcard_WhenReferencedTableHasSensitiveColumn()
    {
        var store = new InMemoryMcpSensitiveColumnStore();
        await store.UpsertAsync(new McpSensitiveColumnEntity { ColumnName = "password" });
        var policy = Policy(store);

        var wildcard = ReadOnlySqlValidator.Validate("SELECT * FROM users");
        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            policy.EnsureQueryAllowedAsync("default", wildcard));
    }

    [Fact]
    public async Task SensitiveColumnPolicy_AllowsColumn_WhenManualOverridePermitsIt()
    {
        var store = new InMemoryMcpSensitiveColumnStore();
        await store.UpsertAsync(new McpSensitiveColumnEntity { ColumnName = "password" });
        await store.UpsertAsync(new McpSensitiveColumnEntity
        {
            SourceKey = "default",
            TableName = "users",
            ColumnName = "password",
            Allowed = true,
            IsManual = true,
        });
        var policy = Policy(store);

        var parsed = ReadOnlySqlValidator.Validate("SELECT password FROM users");
        // Should not throw — the manual override explicitly permits this column for this source.
        await policy.EnsureQueryAllowedAsync("default", parsed);
    }

    [Fact]
    public async Task SensitiveColumnPolicy_AllowsNonSensitiveQuery()
    {
        var store = new InMemoryMcpSensitiveColumnStore();
        await store.UpsertAsync(new McpSensitiveColumnEntity { ColumnName = "password" });
        var policy = Policy(store);

        var parsed = ReadOnlySqlValidator.Validate("SELECT id, email FROM users");
        await policy.EnsureQueryAllowedAsync("default", parsed);
    }

    [Fact]
    public async Task SensitiveColumnPolicy_ReusesCachedSnapshot_UntilRevisionChanges()
    {
        var store = new CountingSensitiveColumnStore(new McpSensitiveColumnEntity { ColumnName = "password" });
        var policy = Policy(store);
        var parsed = ReadOnlySqlValidator.Validate("SELECT id FROM users");

        await policy.EnsureQueryAllowedAsync("default", parsed);
        await policy.EnsureQueryAllowedAsync("default", parsed);
        store.ListCallCount.ShouldBe(1);

        // Sensitive-column metadata changed -> revision rolls -> snapshot must be rebuilt.
        store.Revision = "r2";
        await policy.EnsureQueryAllowedAsync("default", parsed);
        store.ListCallCount.ShouldBe(2);
    }

    [Fact]
    public async Task ProviderCredentialStore_IsolatesCredentialsPerUser()
    {
        var store = new InMemoryMcpProviderCredentialStore();
        await store.UpsertAsync(
            new McpProviderCredentialEntity { ProviderKey = "shortcut", Username = "alice", CredentialKey = "apiToken" },
            "alice-token");

        (await store.GetValueAsync("shortcut", "alice", "apiToken")).ShouldBe("alice-token");
        (await store.GetValueAsync("shortcut", "bob", "apiToken")).ShouldBeNull();
        (await store.ListForUserAsync("bob")).ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchShortcutEpicsAsync_FailsClosed_WhenNoUserOrNoCredential()
    {
        var service = ShortcutService(new InMemoryMcpProviderCredentialStore(), new EmptyHttpClientFactory());

        var noUser = await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.SearchShortcutEpicsAsync(null, null, CancellationToken.None));
        noUser.Message.ShouldContain("signed-in user");

        var noCredential = await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.SearchShortcutEpicsAsync(null, "bob", CancellationToken.None));
        noCredential.Message.ShouldContain("No Shortcut credential");
    }

    [Fact]
    public async Task SearchShortcutEpicsAsync_UsesCallingUsersOwnCredential()
    {
        var credentials = new InMemoryMcpProviderCredentialStore();
        await credentials.UpsertAsync(
            new McpProviderCredentialEntity { ProviderKey = "shortcut", Username = "alice", CredentialKey = "apiToken" },
            "alice-token");
        var service = ShortcutService(credentials, new EmptyHttpClientFactory());

        // Bob has no credential -> denied at the credential gate.
        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.SearchShortcutEpicsAsync(null, "bob", CancellationToken.None));

        // Alice has her own credential -> passes the gate (then fails later in the HTTP layer,
        // which is NOT a policy exception). Proves the call runs as the calling user.
        var aliceFailure = await Should.ThrowAsync<Exception>(() =>
            service.SearchShortcutEpicsAsync(null, "alice", CancellationToken.None));
        aliceFailure.ShouldNotBeOfType<McpHubProviderPolicyException>();
    }

    [Fact]
    public async Task ValidateShortcutCredentialAsync_CapturesProviderIdentity_OnSuccess()
    {
        var factory = new StubHttpClientFactory(
            new Uri("https://api.app.shortcut.com/api/v3/"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"name":"Jane Doe","mention_name":"jane"}"""),
            });
        var service = ShortcutService(new InMemoryMcpProviderCredentialStore(), factory);

        var result = await service.ValidateShortcutCredentialAsync("a-real-token");

        result.IsValid.ShouldBeTrue();
        result.ProviderIdentity.ShouldBe("Jane Doe (@jane)");
    }

    [Fact]
    public async Task ValidateShortcutCredentialAsync_ReportsInvalid_OnRejection()
    {
        var factory = new StubHttpClientFactory(
            new Uri("https://api.app.shortcut.com/api/v3/"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        var service = ShortcutService(new InMemoryMcpProviderCredentialStore(), factory);

        var result = await service.ValidateShortcutCredentialAsync("bad-token");

        result.IsValid.ShouldBeFalse();
        (await service.ValidateShortcutCredentialAsync("  ")).IsValid.ShouldBeFalse();
    }

    private static McpHubService ShortcutService(
        IMcpProviderCredentialStore credentials,
        IHttpClientFactory httpClientFactory)
    {
        var hubStore = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "shortcut", Enabled = true }],
        };
        return new McpHubService(
            hubStore,
            new EmptyProjectQueryService(),
            httpClientFactory,
            Policy(new InMemoryMcpSensitiveColumnStore()),
            ExposurePolicy(),
            credentials,
            NullLogger<McpHubService>.Instance);
    }

    [Fact]
    public async Task McpHubServer_AuditsPolicyDenial_WhenProviderCallIsBlockedByPolicy()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "rabbitmq", Enabled = true }]
        };
        var server = Server(store);

        // No allowedQueues policy -> the RabbitMQ call is denied by policy.
        await server.PeekRabbitMqQueue("vhost", "queue", 5, CancellationToken.None);

        var audit = store.Audit.ShouldHaveSingleItem();
        audit.ProviderKey.ShouldBe("rabbitmq");
        audit.ToolName.ShouldBe("rabbitmq_peek_queue");
        audit.Success.ShouldBeFalse();
        audit.StatusClass.ShouldBe("policy_denied");
        audit.AuthorizationDecision.ShouldBe("denied");
    }

    [Fact]
    public async Task McpHubServer_AuditsProviderError_WhenProviderCallThrows()
    {
        var store = new RecordingMcpHubStore
        {
            Providers = [new() { ProviderKey = "mysql", Enabled = true }]
        };
        // EmptyProjectQueryService throws NotSupportedException for schema listing.
        var server = Server(store);

        await server.ListMySqlSchemas(null, null, null, 1, 25, CancellationToken.None);

        var audit = store.Audit.ShouldHaveSingleItem();
        audit.ProviderKey.ShouldBe("mysql");
        audit.Success.ShouldBeFalse();
        audit.StatusClass.ShouldBe("provider_error");
        audit.AuthorizationDecision.ShouldBe("allowed");
    }

    [Fact]
    public async Task McpHubService_AuditAsync_NormalizesAndPersistsTheEnvelope()
    {
        var store = new RecordingMcpHubStore();
        var service = new McpHubService(
            store,
            new EmptyProjectQueryService(),
            new EmptyHttpClientFactory(),
            Policy(new InMemoryMcpSensitiveColumnStore()),
            ExposurePolicy(),
            new InMemoryMcpProviderCredentialStore(),
            NullLogger<McpHubService>.Instance);

        await service.AuditAsync(
            "  Alice  ", 7, "shortcut", "shortcut_search_epics", "invoke", "Search",
            "epics", "Delegated", "Allowed", "OK", 42, true, null, CancellationToken.None);

        var audit = store.Audit.ShouldHaveSingleItem();
        audit.Username.ShouldBe("alice");
        audit.TokenId.ShouldBe(7);
        audit.CredentialMode.ShouldBe("delegated");
        audit.AuthorizationDecision.ShouldBe("allowed");
        audit.StatusClass.ShouldBe("ok");
        audit.Success.ShouldBeTrue();
        audit.DurationMs.ShouldBe(42);
    }

    private static McpHubServer Server(RecordingMcpHubStore store)
    {
        var hub = new McpHubService(
            store,
            new EmptyProjectQueryService(),
            new EmptyHttpClientFactory(),
            Policy(new InMemoryMcpSensitiveColumnStore()),
            ExposurePolicy(),
            new InMemoryMcpProviderCredentialStore(),
            NullLogger<McpHubService>.Instance);
        return new McpHubServer(hub, new HttpContextAccessor());
    }

    private static SensitiveColumnPolicy Policy(IMcpSensitiveColumnStore store)
    {
        var provider = new ServiceCollection()
            .AddSingleton(store)
            .BuildServiceProvider();
        return new SensitiveColumnPolicy(provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static MySqlSourceExposurePolicy ExposurePolicy(params DatabaseSourceEntity[] sources)
    {
        var provider = new ServiceCollection()
            .AddSingleton<IDatabaseSourceStore>(new FakeDatabaseSourceStore(sources))
            .BuildServiceProvider();
        return new MySqlSourceExposurePolicy(provider.GetRequiredService<IServiceScopeFactory>());
    }

    private sealed class FakeDatabaseSourceStore(IReadOnlyList<DatabaseSourceEntity> sources) : IDatabaseSourceStore
    {
        public Task<IReadOnlyList<DatabaseSourceEntity>> ListAsync() => Task.FromResult(sources);
        public Task<DatabaseSourceEntity?> GetAsync(long id) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity> CreateAsync(DatabaseSourceEntity entity) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity?> UpdateAsync(long id, string? serverName, string? databaseName, string? connectionString, bool? enabled) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity?> UpdateMcpExposureAsync(long id, bool? mcpHubEnabled, string? mcpExposureMode, string? mcpDisplayName, string? mcpEnvironment) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(long id) => throw new NotSupportedException();
        public Task UpdateLastSyncedAsync(long id) => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory(Uri baseAddress, Func<HttpRequestMessage, HttpResponseMessage> responder)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(responder)) { BaseAddress = baseAddress };

        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(responder(request));
        }
    }

    private sealed class CountingSensitiveColumnStore(params McpSensitiveColumnEntity[] seed) : IMcpSensitiveColumnStore
    {
        private readonly List<McpSensitiveColumnEntity> rows = seed.ToList();

        public int ListCallCount { get; private set; }

        public string Revision { get; set; } = "r1";

        public Task<IReadOnlyList<McpSensitiveColumnEntity>> ListAsync(CancellationToken ct = default)
        {
            ListCallCount++;
            return Task.FromResult<IReadOnlyList<McpSensitiveColumnEntity>>(rows.ToList());
        }

        public Task UpsertAsync(McpSensitiveColumnEntity entity, CancellationToken ct = default)
        {
            rows.Add(entity);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(long id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<string> GetRevisionAsync(CancellationToken ct = default) => Task.FromResult(Revision);
    }

    private sealed class RecordingMcpHubStore : IMcpHubStore
    {
        public List<McpHubProviderEntity> Providers { get; init; } = [];
        public List<McpHubToolEntity> Tools { get; } = [];
        public Dictionary<(string Provider, string Key), string?> Config { get; } = [];
        public Dictionary<(string Provider, string Key), string?> Credentials { get; } = [];
        public List<McpHubAuditEntity> Audit { get; } = [];

        public Task<IReadOnlyList<McpHubProviderEntity>> ListProvidersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpHubProviderEntity>>(Providers);

        public Task<IReadOnlyList<McpHubToolEntity>> ListToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpHubToolEntity>>(Tools);

        public Task UpsertProviderAsync(McpHubProviderEntity provider, CancellationToken ct = default)
        {
            Providers.RemoveAll(item => item.ProviderKey == provider.ProviderKey);
            Providers.Add(provider);
            return Task.CompletedTask;
        }

        public Task UpsertToolAsync(McpHubToolEntity tool, CancellationToken ct = default)
        {
            Tools.RemoveAll(item => item.ToolName == tool.ToolName);
            Tools.Add(tool);
            return Task.CompletedTask;
        }

        public Task<string?> GetConfigValueAsync(string providerKey, string configKey, CancellationToken ct = default) =>
            Task.FromResult(Config.GetValueOrDefault((providerKey, configKey)));

        public Task<string?> GetCredentialValueAsync(string providerKey, string credentialKey, CancellationToken ct = default) =>
            Task.FromResult(Credentials.GetValueOrDefault((providerKey, credentialKey)));

        public Task<bool> SetProviderEnabledAsync(string providerKey, bool enabled, bool? sourceVisible, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateToolCatalogStateAsync(string toolName, bool? enabled, bool? defaultSelected, string? accessClass, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpHubCredentialEntity>> ListCredentialsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetCredentialValueAsync(string providerKey, string credentialKey, string? value, string? updatedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpHubConfigEntity>> ListConfigAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetConfigValueAsync(string providerKey, string configKey, string? value, string? updatedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceTokenEntitlementsAsync(long tokenId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetTokenEntitlementsAsync(long tokenId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsTokenEntitledAsync(long tokenId, string toolName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CreateAuditAsync(McpHubAuditEntity audit, CancellationToken ct = default)
        {
            Audit.Add(audit);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<McpHubAuditEntity>> ListAuditAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpHubAuditEntity>>(Audit);
    }

    private sealed class EmptyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class EmptyProjectQueryService : IProjectQueryService
    {
        public Task<ProjectListResponse> ListAsync(string? search, string? group, int page, int pageSize) => throw new NotSupportedException();
        public Task<SchemaListResponse> ListSchemasAsync(string? search, string? server, string? database, int page, int pageSize) => throw new NotSupportedException();
        public Task<SchemaCatalogResponse?> GetSchemaCatalogAsync(string name) => throw new NotSupportedException();
        public Task<ProjectDetailResponse?> GetDetailAsync(string name) => throw new NotSupportedException();
        public Task<ProjectHealthResponse?> GetHealthAsync(string name) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileMetrics>> GetMetricsAsync(string name, string? dotnetProject, int top) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileMetrics>> GetHotspotsAsync(string name, int top) => throw new NotSupportedException();
        public Task<NodeListResponse> GetNodesAsync(string name, string? label, string? dotnetProject, int page, int pageSize) => throw new NotSupportedException();
        public Task<AnalysisBatchResponse?> GetBatchStatusAsync(string name) => throw new NotSupportedException();
        public Task<ProjectSecurityResponse?> GetSecurityAsync(string name) => throw new NotSupportedException();
        public Task<string?> GetReadmeAsync(string name) => throw new NotSupportedException();
    }
}

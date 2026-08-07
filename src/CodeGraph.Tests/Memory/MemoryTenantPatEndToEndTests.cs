using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CodeGraph.Api.Auth;
using CodeGraph.Api.Memory;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Memory;
using CodeGraph.Models.Messages;
using CodeGraph.Services;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Messaging;
using CodeGraph.Tests.Data;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Memory;

public class MemoryTenantPatEndToEndTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task LocalMemoryPipeline_IsolatesTwoMcpPatUsersEndToEnd()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var sourceBuilder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_memory_pat_test_{Guid.NewGuid():N}";
        sourceBuilder.Database = databaseName;
        var databaseConnectionString = sourceBuilder.ConnectionString;
        var encryptionKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var migrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations");
        var migrationOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = databaseConnectionString,
            MigrationsPath = migrationsPath,
        });

        var runner = new MariaDbMigrationRunner(
            migrationOptions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MariaDbMigrationRunner>.Instance);

        ServiceProvider? provider = null;
        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddRouting();
            services.AddSingleton(new DiagnosticListener("memory-pat-test"));
            services.AddSingleton<DiagnosticSource>(sp => sp.GetRequiredService<DiagnosticListener>());
            services.AddCodeGraphMariaDbData(options =>
            {
                options.ConnectionString = databaseConnectionString;
                options.MigrationsPath = migrationsPath;
                options.EncryptionKey = encryptionKey;
            });
            services.Configure<CodeGraphStorageOptions>(options =>
            {
                options.Provider = "MariaDb";
                options.MariaDbConnectionString = databaseConnectionString;
                options.MariaDbEncryptionKey = encryptionKey;
            });
            services.AddTransient<McpPersonalAccessTokenService>();
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestDefaultAuthenticationHandler.AuthScheme;
                    options.DefaultChallengeScheme = TestDefaultAuthenticationHandler.AuthScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestDefaultAuthenticationHandler>(
                    TestDefaultAuthenticationHandler.AuthScheme,
                    _ => { })
                .AddScheme<AuthenticationSchemeOptions, McpPatAuthenticationHandler>(
                    McpPatAuthenticationDefaults.Scheme,
                    _ => { });
            services.AddAuthorization(options =>
                options.AddPolicy(McpPatAuthenticationDefaults.Policy, policy =>
                {
                    policy.AuthenticationSchemes.Add(McpPatAuthenticationDefaults.Scheme);
                    policy.RequireAuthenticatedUser();
                }));
            services.AddSingleton<IEmbeddingService, UnavailableEmbeddingService>();
            services.AddTransient<MemoryClaimIngestionService>();
            services.AddTransient<MemoryLegacyMigrationService>();
            services.AddTransient<MemoryObservationMigrationService>();
            services.AddTransient<MemoryRetrievalService>();
            services.AddTransient<MemoryService>();
            services.AddTransient<IMemoryOperationsService, LocalMemoryOperationsService>();
            services.AddSingleton<InlineMemoryMessageBus>();
            services.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<InlineMemoryMessageBus>());
            provider = services.BuildServiceProvider();

            string aliceToken;
            string bobToken;
            using (var scope = provider.CreateScope())
            {
                var tokens = scope.ServiceProvider.GetRequiredService<McpPersonalAccessTokenService>();
                aliceToken = (await tokens.CreateForUserAsync("alice", "alice test", 30)).RawToken!;
                bobToken = (await tokens.CreateForUserAsync("bob", "bob test", 30)).RawToken!;
            }

            var application = new ApplicationBuilder(provider);
            application.UseRouting();
            application.UseAuthentication();
            application.UseAuthorization();
            application.UseMiddleware<MemoryTenantScopeMiddleware>();
            application.UseEndpoints(endpoints =>
            {
                var group = endpoints.MapGroup("/mcp-local")
                    .RequireAuthorization(McpPatAuthenticationDefaults.Policy);
                group.MapPost("/store", StoreAsync);
                group.MapGet("/receipts/{receiptId}", async (
                    string receiptId,
                    IMemoryOperationsService operations,
                    CancellationToken ct) =>
                {
                    var receipt = await operations.GetWriteReceiptAsync(receiptId, ct);
                    return receipt is null ? Results.NotFound() : Results.Ok(receipt);
                });
                group.MapGet("/search", async (
                    string query,
                    IMemoryOperationsService operations,
                    CancellationToken ct) => Results.Ok(await operations.SearchMemoryAsync(query, 10, 10, ct)));
                group.MapGet("/entities/{entityId}", async (
                    string entityId,
                    IMemoryOperationsService operations,
                    CancellationToken ct) =>
                {
                    var bundle = await operations.GetEntityBundleAsync(entityId, false, true, 20, ct);
                    return bundle is null ? Results.NotFound() : Results.Ok(bundle);
                });
                group.MapGet("/claims/{claimId}", async (
                    string claimId,
                    IMemoryOperationsService operations,
                    CancellationToken ct) =>
                {
                    var bundle = await operations.GetClaimBundleAsync(claimId, true, true, true, ct);
                    return bundle is null ? Results.NotFound() : Results.Ok(bundle);
                });
                group.MapPost("/cleanup/{source}", async (
                    string source,
                    IMemoryOperationsService operations,
                    CancellationToken ct) => Results.Ok(await operations.DeleteMemoryBySourceAsync(source, false, ct)));
            });
            var pipeline = application.Build();

            var aliceAck = (await SendAsync(provider, pipeline, aliceToken, HttpMethods.Post, "/mcp-local/store",
                CreateTenantMemory("alice", "Alice private memory"))).Json<MemoryStoreAcceptedResult>();
            (await SendAsync(provider, pipeline, aliceToken, HttpMethods.Get,
                    $"/mcp-local/receipts/{aliceAck.ReceiptId}"))
                .Json<MemoryWriteReceipt>().Status.ShouldBe(MemoryWriteReceiptStatus.Completed);
            (await SendAsync(provider, pipeline, aliceToken, HttpMethods.Get,
                    "/mcp-local/search?query=private"))
                .Json<MemorySearchResult>().Entities.Single().Label.ShouldBe("Alice private memory");
            (await SendAsync(provider, pipeline, aliceToken, HttpMethods.Get,
                    "/mcp-local/entities/shared_entity"))
                .Json<MemoryEntityBundle>().Entity.Label.ShouldBe("Alice private memory");
            (await SendAsync(provider, pipeline, aliceToken, HttpMethods.Get,
                    "/mcp-local/claims/shared_claim"))
                .Json<MemoryClaimBundle>().Evidence.Single().SourceRef.ShouldBe("alice-evidence");

            (await SendAsync(provider, pipeline, bobToken, HttpMethods.Get,
                    $"/mcp-local/receipts/{aliceAck.ReceiptId}"))
                .StatusCode.ShouldBe(StatusCodes.Status404NotFound);
            (await SendAsync(provider, pipeline, bobToken, HttpMethods.Get,
                    "/mcp-local/search?query=private"))
                .Json<MemorySearchResult>().Entities.ShouldBeEmpty();
            (await SendAsync(provider, pipeline, bobToken, HttpMethods.Get,
                    "/mcp-local/entities/shared_entity"))
                .StatusCode.ShouldBe(StatusCodes.Status404NotFound);
            (await SendAsync(provider, pipeline, bobToken, HttpMethods.Get,
                    "/mcp-local/claims/shared_claim"))
                .StatusCode.ShouldBe(StatusCodes.Status404NotFound);

            var bobAck = (await SendAsync(provider, pipeline, bobToken, HttpMethods.Post, "/mcp-local/store",
                CreateTenantMemory("bob", "Bob private memory"))).Json<MemoryStoreAcceptedResult>();
            bobAck.ReceiptId.ShouldNotBe(aliceAck.ReceiptId);
            (await SendAsync(provider, pipeline, bobToken, HttpMethods.Get,
                    "/mcp-local/entities/shared_entity"))
                .Json<MemoryEntityBundle>().Entity.Label.ShouldBe("Bob private memory");
            (await SendAsync(provider, pipeline, bobToken, HttpMethods.Get,
                    "/mcp-local/claims/shared_claim"))
                .Json<MemoryClaimBundle>().Evidence.Single().SourceRef.ShouldBe("bob-evidence");

            (await SendAsync(provider, pipeline, bobToken, HttpMethods.Post, "/mcp-local/cleanup/pat-isolation"))
                .Json<MemoryCleanupResult>().EntitiesDeleted.ShouldBe(1);
            (await SendAsync(provider, pipeline, bobToken, HttpMethods.Get,
                    "/mcp-local/entities/shared_entity"))
                .StatusCode.ShouldBe(StatusCodes.Status404NotFound);
            (await SendAsync(provider, pipeline, bobToken, HttpMethods.Get,
                    $"/mcp-local/receipts/{bobAck.ReceiptId}"))
                .StatusCode.ShouldBe(StatusCodes.Status404NotFound);

            (await SendAsync(provider, pipeline, aliceToken, HttpMethods.Get,
                    "/mcp-local/entities/shared_entity"))
                .Json<MemoryEntityBundle>().Entity.Label.ShouldBe("Alice private memory");
            (await SendAsync(provider, pipeline, aliceToken, HttpMethods.Get,
                    "/mcp-local/claims/shared_claim"))
                .Json<MemoryClaimBundle>().Evidence.Single().SourceRef.ShouldBe("alice-evidence");
            (await SendAsync(provider, pipeline, aliceToken, HttpMethods.Get,
                    $"/mcp-local/receipts/{aliceAck.ReceiptId}"))
                .Json<MemoryWriteReceipt>().Status.ShouldBe(MemoryWriteReceiptStatus.Completed);
        }
        finally
        {
            if (provider is not null)
                await provider.DisposeAsync();
            await DropDatabaseAsync(sourceBuilder.ConnectionString, databaseName);
        }
    }

    private static async Task<IResult> StoreAsync(
        MemoryClaimExtractionResult extraction,
        IMemoryOperationsService operations,
        MemoryService memoryService,
        InlineMemoryMessageBus messageBus,
        IMemoryTenantContext tenantContext,
        CancellationToken ct)
    {
        var ack = await operations.QueueClaimsAsync(extraction, "pat-isolation", "typed", ct);
        var message = messageBus.Take(ack.ReceiptId);
        using (tenantContext.Enter(message.Username))
        {
            await memoryService.MarkWriteReceiptProcessingAsync(message.ReceiptId!);
            try
            {
                var result = await memoryService.StoreClaimsAsync(message.Extraction, message.Source);
                await memoryService.CompleteWriteReceiptAsync(message.ReceiptId!, result);
            }
            catch (Exception ex)
            {
                await memoryService.FailWriteReceiptAsync(message.ReceiptId!, ex.Message);
                throw;
            }
        }

        return Results.Ok(ack);
    }

    private static MemoryClaimExtractionResult CreateTenantMemory(string owner, string label) => new()
    {
        Entities =
        [
            new MemoryExtractedEntity
            {
                Id = "shared-entity",
                Label = label,
                Type = "private",
            }
        ],
        Claims =
        [
            new MemoryExtractedClaim
            {
                Id = "shared-claim",
                Subject = "shared-entity",
                Predicate = "owned_by",
                ValueText = owner,
                NormalizedText = $"shared entity owned by {owner}",
            }
        ],
        Evidence =
        [
            new MemoryExtractedEvidence
            {
                ClaimId = "shared-claim",
                EvidenceType = "test",
                SourceRef = $"{owner}-evidence",
            }
        ],
    };

    private static async Task<PipelineResponse> SendAsync(
        IServiceProvider rootProvider,
        RequestDelegate pipeline,
        string token,
        string method,
        string pathAndQuery,
        object? body = null)
    {
        using var scope = rootProvider.CreateScope();
        var uri = new Uri("http://localhost" + pathAndQuery);
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Method = method;
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        context.Request.Headers.Authorization = $"Bearer {token}";
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
            var payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
            context.Request.Body = new MemoryStream(payload);
            context.Request.ContentLength = payload.Length;
            context.Request.ContentType = "application/json";
        }

        await pipeline(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return new PipelineResponse(context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Database = "" };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }

    private sealed record PipelineResponse(int StatusCode, string Body)
    {
        public T Json<T>()
        {
            StatusCode.ShouldBe(StatusCodes.Status200OK, Body);
            return JsonSerializer.Deserialize<T>(Body, JsonOptions)
                   ?? throw new InvalidOperationException("Pipeline response body was empty.");
        }
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class InlineMemoryMessageBus : IMessageBus
    {
        private readonly Dictionary<string, StoreMemoryClaims> messages = new(StringComparer.Ordinal);

        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        {
            if (message is StoreMemoryClaims claims && !string.IsNullOrWhiteSpace(claims.ReceiptId))
                messages.Add(claims.ReceiptId, claims);
            return Task.CompletedTask;
        }

        public StoreMemoryClaims Take(string receiptId)
        {
            var message = messages[receiptId];
            messages.Remove(receiptId);
            return message;
        }
    }

    private sealed class UnavailableEmbeddingService : IEmbeddingService
    {
        public bool IsAvailable => false;
        public string ModelName => "unavailable";
        public int Dimensions => 0;
        public float[] GenerateEmbedding(string text) => [];
        public IReadOnlyList<float[]> GenerateEmbeddings(IReadOnlyList<string> texts) => [];
    }

    private sealed class TestDefaultAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthScheme = "TestDefault";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim("preferred_username", "shared-local-development-user"),
                new Claim(ClaimTypes.Name, "shared-local-development-user"),
            ], AuthScheme);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), AuthScheme);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}

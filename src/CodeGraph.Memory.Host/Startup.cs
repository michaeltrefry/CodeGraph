using System.Security.Claims;
using System.Text.Json.Serialization;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Data.Neo4j;
using CodeGraph.Host.Shared.Auth;
using CodeGraph.Host.Shared.Hosting;
using CodeGraph.Memory.Host.Consumers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Messaging;
using MassTransit;
using Microsoft.Extensions.Options;

namespace CodeGraph.Memory.Host;

public static class Startup
{
    public const int Port = 5039;
    public const string InternalServiceAudience = "codegraph-memory";

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddCodeGraphOptions(configuration);
        services.AddCodeGraphHostShared(configuration, "CodeGraph.Memory.Host");
        services.AddHttpClient();

        services
            .AddMvc()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        services.AddCors(opts => opts.AddDefaultPolicy(policy =>
            policy.WithOrigins($"http://localhost:{Port}")
                .AllowAnyHeader()
                .AllowAnyMethod()));

        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
            {
                Title = "CodeGraph Memory Host",
                Version = "v1",
                Description = "Standalone claim-centric memory host for CodeGraph"
            });
        });

        RegisterPersistence(services, configuration);
        RegisterMemoryServices(services);
        RegisterMassTransit(services);
    }

    public static void Configure(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "CodeGraph Memory Host v1"));
        }

        app.UseCors();
        app.UseRouting();
        app.UseInternalServiceAuthentication();
        app.MapControllers();
        app.MapHealthChecks("/health");
    }

    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var serviceProvider = scope.ServiceProvider;
        var hostEnvironment = serviceProvider.GetRequiredService<IHostEnvironment>();
        var storageOptions = serviceProvider.GetRequiredService<IOptions<CodeGraphStorageOptions>>().Value;
        var migrationRunner = serviceProvider.GetRequiredService<IMigrationRunner>();
        var configuredMigrationsPath = IsMariaDbProvider(storageOptions)
            ? storageOptions.MariaDbMigrationsPath
            : storageOptions.Neo4jMigrationsPath;
        var migrationsPath = ResolveMigrationsPath(hostEnvironment.ContentRootPath, configuredMigrationsPath);
        await migrationRunner.ApplyMigrationsAsync(migrationsPath);
    }

    private static void RegisterMemoryServices(IServiceCollection services)
    {
        services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
        services.AddTransient<MemoryClaimIngestionService>();
        services.AddTransient<MemoryLegacyMigrationService>();
        services.AddTransient<MemoryObservationMigrationService>();
        services.AddTransient<MemoryRetrievalService>();
        services.AddTransient<MemoryService>();
        services.AddTransient<IMessageBus, MassTransitMessageBus>();
    }

    private static void RegisterMassTransit(IServiceCollection services)
    {
        services.AddMassTransit(x =>
        {
            x.AddConsumer<StoreMemoryClaimsConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbitOptions = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
                cfg.Host(rabbitOptions.Host, "/", h =>
                {
                    h.Username(rabbitOptions.Username);
                    h.Password(rabbitOptions.Password);
                });

                var consumerOptions = context.GetRequiredService<IOptions<ConsumerOptions>>().Value;
                cfg.ReceiveEndpoint("store-memory-claims", e =>
                {
                    ConsumerConfiguration.ConfigureStandardRetries(e, consumerOptions);
                    e.ConfigureConsumer<StoreMemoryClaimsConsumer>(context);
                });
            });
        });
    }

    private static void RegisterPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var storageOptions = configuration
            .GetSection($"{CodeGraphOptionsServiceCollectionExtensions.SectionName}:{nameof(CodeGraphServiceSettings.StorageOptions)}")
            .Get<CodeGraphStorageOptions>() ?? new CodeGraphStorageOptions();

        if (IsMariaDbProvider(storageOptions))
        {
            services.AddCodeGraphMariaDbData(options =>
            {
                options.ConnectionString = storageOptions.MariaDbConnectionString;
                options.MigrationsPath = storageOptions.MariaDbMigrationsPath;
                options.EncryptionKey = storageOptions.MariaDbEncryptionKey;
            });
            return;
        }

        services.AddSingleton<Neo4jSessionFactory>();
        services.AddTransient<IGraphStore, Neo4jGraphStore>();
        services.AddTransient<IMigrationRunner>(sp => sp.GetRequiredService<IGraphStore>());
        services.AddTransient<IVectorStore, Neo4jVectorStore>();
        services.AddTransient<IMemoryGraphStore, Neo4jMemoryGraphStore>();
    }

    private static bool IsMariaDbProvider(CodeGraphStorageOptions storageOptions) =>
        storageOptions.Provider.Equals("MariaDb", StringComparison.OrdinalIgnoreCase)
        || storageOptions.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase);

    private static string ResolveMigrationsPath(string contentRootPath, string migrationsPath)
    {
        if (Path.IsPathRooted(migrationsPath))
            return migrationsPath;

        var contentRelativePath = Path.GetFullPath(Path.Combine(contentRootPath, migrationsPath));
        if (Directory.Exists(contentRelativePath))
            return contentRelativePath;

        var directory = new DirectoryInfo(contentRootPath);
        while (directory.Parent is not null)
        {
            directory = directory.Parent;
            var ancestorRelativePath = Path.GetFullPath(Path.Combine(directory.FullName, migrationsPath));
            if (Directory.Exists(ancestorRelativePath))
                return ancestorRelativePath;
        }

        return contentRelativePath;
    }
}

internal static class InternalServiceAuthenticationApplicationBuilderExtensions
{
    public static IApplicationBuilder UseInternalServiceAuthentication(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (HttpMethods.IsOptions(context.Request.Method) ||
                context.Request.Path.StartsWithSegments("/health") ||
                context.Request.Path.StartsWithSegments("/swagger"))
            {
                await next();
                return;
            }

            var authOptions = context.RequestServices.GetRequiredService<IOptions<InternalServiceAuthOptions>>().Value;
            if (!authOptions.Enabled)
            {
                context.User = CreatePrincipal("local-memory", "LocalInternalService");
                await next();
                return;
            }

            var validator = context.RequestServices.GetRequiredService<IInternalServiceTokenValidator>();
            var token = context.Request.Headers[authOptions.HeaderName].ToString();
            var validation = validator.ValidateToken(token, Startup.InternalServiceAudience);
            if (!validation.IsValid)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized", message = validation.Error });
                return;
            }

            context.User = validation.Principal ?? CreatePrincipal("unknown", "CodeGraphInternalService");
            await next();
        });
    }

    private static ClaimsPrincipal CreatePrincipal(string username, string authenticationType)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, username),
            new Claim("preferred_username", username),
            new Claim("codegraph_internal", "true")
        ], authenticationType));
    }
}

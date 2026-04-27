using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using CodeGraph.Api;
using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace CodeGraph.Tests.Services;

public class ApiStartupAdminAuthTests
{
    [Fact]
    public async Task ConfigureServices_RegistersCoreServicesWithoutAdminAuth()
    {
        var services = new ServiceCollection();

        var configuration = CreateConfiguration();

        Startup.ConfigureServices(services, configuration);

        services.Any(d => d.ServiceType.Name == "AdminAccessFilter").ShouldBeFalse();
        services.Any(d => d.ServiceType.Name == "IAdminUserService").ShouldBeFalse();
        services.Any(d => d.ServiceType.Name == "IAdminStore").ShouldBeFalse();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAnalysisProviderRegistry>().GetProvider().ProviderName.ShouldBe("anthropic");
    }

    [Fact]
    public async Task ConfigureServices_BindsStandaloneAuthOptions_AndRegistersPolicies()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["CodeGraph:AuthOptions:Enabled"] = "true",
            ["CodeGraph:AuthOptions:Authority"] = "https://identity.trefry.net/realms/trefry",
            ["CodeGraph:AuthOptions:Audience"] = "codegraph-api",
            ["CodeGraph:AuthOptions:ClientId"] = "codegraph-web",
            ["CodeGraph:AuthOptions:Scope"] = "openid profile email",
            ["CodeGraph:AuthOptions:AllowedOrigins:0"] = "https://codegraph.trefry.net"
        });

        Startup.ConfigureServices(services, configuration);

        await using var provider = services.BuildServiceProvider();
        var authOptions = provider.GetRequiredService<IOptions<AuthOptions>>().Value;
        authOptions.Enabled.ShouldBeTrue();
        authOptions.Authority.ShouldBe("https://identity.trefry.net/realms/trefry");
        authOptions.Audience.ShouldBe("codegraph-api");
        authOptions.ClientId.ShouldBe("codegraph-web");
        authOptions.Scope.ShouldBe("openid profile email");
        authOptions.AllowedOrigins.ShouldBe(["https://codegraph.trefry.net"]);

        var authorizationOptions = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        authorizationOptions.GetPolicy(CodeGraphAuthenticationDefaults.UserPolicy).ShouldNotBeNull();
        authorizationOptions.GetPolicy(CodeGraphAuthenticationDefaults.AdminPolicy).ShouldNotBeNull();
        authorizationOptions.GetPolicy(McpPatAuthenticationDefaults.Policy).ShouldNotBeNull();
        authorizationOptions.FallbackPolicy.ShouldNotBeNull();
    }

    [Fact]
    public async Task ConfigureServices_AllowsLoopbackAngularDevOrigins()
    {
        var services = new ServiceCollection();

        Startup.ConfigureServices(services, CreateConfiguration());

        await using var provider = services.BuildServiceProvider();
        var corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
        var defaultPolicy = corsOptions.GetPolicy(corsOptions.DefaultPolicyName);

        defaultPolicy.ShouldNotBeNull();
        defaultPolicy.Origins.ShouldContain("http://localhost:4200");
        defaultPolicy.Origins.ShouldContain("http://127.0.0.1:4200");
    }

    [Fact]
    public async Task AdminPolicy_AllowsLocalDevAdminWithoutDatabaseStore()
    {
        var services = new ServiceCollection();
        Startup.ConfigureServices(services, CreateConfiguration());

        await using var provider = services.BuildServiceProvider();
        var authorizationService = provider.GetRequiredService<IAuthorizationService>();
        var principal = TestPrincipal("local-admin");

        var result = await authorizationService.AuthorizeAsync(
            principal,
            resource: null,
            CodeGraphAuthenticationDefaults.AdminPolicy);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task AdminPolicy_UsesAdminStoreWhenAuthIsEnabled()
    {
        var services = new ServiceCollection();
        Startup.ConfigureServices(services, CreateConfiguration(new Dictionary<string, string?>
        {
            ["CodeGraph:AuthOptions:Enabled"] = "true",
            ["CodeGraph:AuthOptions:Authority"] = "https://identity.trefry.net/realms/trefry"
        }));
        services.AddSingleton<IAdminStore>(new RecordingAdminStore("codex"));

        await using var provider = services.BuildServiceProvider();
        var authorizationService = provider.GetRequiredService<IAuthorizationService>();

        (await authorizationService.AuthorizeAsync(
            TestPrincipal("codex"),
            resource: null,
            CodeGraphAuthenticationDefaults.AdminPolicy)).Succeeded.ShouldBeTrue();

        (await authorizationService.AuthorizeAsync(
            TestPrincipal("not-admin"),
            resource: null,
            CodeGraphAuthenticationDefaults.AdminPolicy)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task ConfigureServices_ValidatesMariaDbScopedProviderGraph()
    {
        var services = new ServiceCollection();
        Startup.ConfigureServices(services, CreateConfiguration(new Dictionary<string, string?>
        {
            ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
            ["CodeGraph:StorageOptions:MariaDbConnectionString"] = "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;"
        }));

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAnalysisProviderRegistry>().ShouldNotBeNull();
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?>? values = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["CodeGraph:AnalysisOptions:DefaultProvider"] = "anthropic",
            ["CodeGraph:StorageOptions:Provider"] = "Neo4j",
            ["CodeGraph:RepositorySource:Provider"] = "Folder",
            ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp"
        };

        if (values is not null)
        {
            foreach (var (key, value) in values)
            {
                configuration[key] = value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configuration)
            .Build();
    }

    private static System.Security.Claims.ClaimsPrincipal TestPrincipal(string username)
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("preferred_username", username)
            ],
            "test");
        return new System.Security.Claims.ClaimsPrincipal(identity);
    }

    private sealed class RecordingAdminStore(string adminUsername) : IAdminStore
    {
        public Task<IReadOnlyList<AdminUserEntity>> ListAdminUsersAsync() =>
            Task.FromResult<IReadOnlyList<AdminUserEntity>>([]);

        public Task<bool> IsAdminAsync(string username) =>
            Task.FromResult(username == adminUsername);

        public Task<AdminUserEntity> AddAdminUserAsync(AdminUserEntity entity) =>
            Task.FromResult(entity);

        public Task<bool> RemoveAdminUserAsync(string username) =>
            Task.FromResult(false);

        public Task<SettingsOverrideEntity?> GetLatestSettingsOverrideAsync() =>
            Task.FromResult<SettingsOverrideEntity?>(null);

        public Task UpsertSettingsOverrideAsync(SettingsOverrideEntity entity) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AgentPromptOverrideEntity>> ListPromptOverridesAsync() =>
            Task.FromResult<IReadOnlyList<AgentPromptOverrideEntity>>([]);

        public Task<AgentPromptOverrideEntity?> GetPromptOverrideAsync(string promptKey) =>
            Task.FromResult<AgentPromptOverrideEntity?>(null);

        public Task UpsertPromptOverrideAsync(AgentPromptOverrideEntity entity) =>
            Task.CompletedTask;

        public Task<bool> DeletePromptOverrideAsync(string promptKey) =>
            Task.FromResult(false);
    }
}

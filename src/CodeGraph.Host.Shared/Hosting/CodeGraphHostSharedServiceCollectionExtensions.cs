using CodeGraph.Host.Shared.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Host.Shared.Hosting;

public static class CodeGraphHostSharedServiceCollectionExtensions
{
    public static IServiceCollection AddCodeGraphHostShared(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        services.AddSingleton(new CodeGraphHostDescriptor(serviceName.Trim()));
        services.AddCodeGraphInternalServiceAuth(configuration);
        services.AddHealthChecks();
        return services;
    }

    public static IServiceCollection AddCodeGraphInternalServiceAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<InternalServiceAuthOptions>()
            .Bind(configuration.GetSection(InternalServiceAuthOptions.SectionPath));

        services.AddSingleton<IInternalServiceTokenFactory, InternalServiceTokenFactory>();
        services.AddSingleton<IInternalServiceTokenValidator, InternalServiceTokenValidator>();
        return services;
    }
}

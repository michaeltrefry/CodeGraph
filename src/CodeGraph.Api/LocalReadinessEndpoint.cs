using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CodeGraph.Api;

/// <summary>
/// Container-local readiness probe used by the production docker healthcheck. Kestrel only starts
/// listening after <see cref="Startup.InitializeAsync"/> has applied MariaDB migrations, so any
/// response means startup finished; the status then mirrors the registered health checks (the
/// MassTransit bus), matching the <c>/health</c> endpoints of the other hosts. The probe is
/// anonymous so the healthcheck needs no credentials, answers only loopback callers, and returns
/// only the aggregate status word.
/// </summary>
public static class LocalReadinessEndpoint
{
    public const string Path = "/health";

    public static IEndpointConventionBuilder MapLocalReadinessEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, async (HttpContext context, HealthCheckService healthChecks) =>
            {
                if (!IsLoopback(context.Connection.RemoteIpAddress))
                    return Results.NotFound();

                var report = await healthChecks.CheckHealthAsync(context.RequestAborted);
                return Results.Text(
                    report.Status.ToString(),
                    statusCode: report.Status == HealthStatus.Unhealthy
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status200OK);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

    internal static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return IPAddress.IsLoopback(address);
    }
}

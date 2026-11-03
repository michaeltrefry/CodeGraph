using CodeGraph.Data;
using CodeGraph.Memory.Host.Controllers;
using CodeGraph.Models.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.MemoryHost;

public class MemoryProjectScopesControllerTests
{
    [Fact]
    public async Task Backfill_RunsForTheSystemIdentityOnly()
    {
        var backfill = new RecordingBackfill();
        var request = new MemoryProjectScopeBackfillRequest { ProjectKey = " CG ", Repositories = ["codegraph", "mlx-rs"] };

        var tenant = new MemoryTenantContext();
        using (tenant.Enter("user:michael"))
            (await CreateController(tenant, backfill).Backfill(request)).ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        backfill.Calls.ShouldBeEmpty();

        (await CreateController(tenant, backfill).Backfill(request)).ShouldBeOfType<NoContentResult>();
        backfill.Calls.ShouldHaveSingleItem().ShouldBe(("CG", "codegraph,mlx-rs"));
    }

    [Fact]
    public async Task Backfill_WithoutAProjectScopeStoreIsNotSupported()
    {
        var result = await CreateController(new MemoryTenantContext(), backfill: null)
            .Backfill(new MemoryProjectScopeBackfillRequest { ProjectKey = "CG", Repositories = ["codegraph"] });

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status501NotImplemented);
    }

    private static MemoryProjectScopesController CreateController(IMemoryTenantContext tenant, IMemoryProjectScopeBackfill? backfill)
    {
        var services = new ServiceCollection();
        if (backfill is not null)
            services.AddSingleton(backfill);
        return new MemoryProjectScopesController(tenant)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() }
            }
        };
    }

    private sealed class RecordingBackfill : IMemoryProjectScopeBackfill
    {
        public List<(string, string)> Calls { get; } = [];

        public Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
        {
            Calls.Add((projectKey, string.Join(",", repositories)));
            return Task.CompletedTask;
        }
    }
}

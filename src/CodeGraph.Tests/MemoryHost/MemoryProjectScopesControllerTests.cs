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

    [Fact]
    public async Task Watermark_AndRangeBackfill_RunForTheSystemIdentityOnly()
    {
        var backfill = new RecordingBackfill();
        var since = new MemoryScopeWatermark(1, 2, 3);
        var upTo = new MemoryScopeWatermark(4, 5, 6);
        var range = new MemoryProjectScopeRangeBackfillRequest
        {
            Projects = [new MemoryProjectScopeBackfillRequest { ProjectKey = " CG ", Repositories = ["codegraph"] }],
            After = since,
            UpTo = upTo,
        };

        var tenant = new MemoryTenantContext();
        using (tenant.Enter("user:michael"))
        {
            (await CreateController(tenant, backfill).Watermark(new MemoryProjectScopeWatermarkRequest { Since = since }))
                .ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
            (await CreateController(tenant, backfill).BackfillRange(range))
                .ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        }
        backfill.Calls.ShouldBeEmpty();

        (await CreateController(tenant, backfill).Watermark(new MemoryProjectScopeWatermarkRequest { Since = since }))
            .ShouldBeOfType<OkObjectResult>().Value.ShouldBe(backfill.Memory);
        (await CreateController(tenant, backfill).BackfillRange(range)).ShouldBeOfType<NoContentResult>();
        backfill.Calls.ShouldBe([("watermark", "1,2,3"), ("range:CG", "codegraph 1,2,3-4,5,6")]);
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

        public MemoryScopeWatermark Memory { get; } = new(7, 8, 9);

        public Task<MemoryScopeWatermark> GetWatermarkAsync(MemoryScopeWatermark? since, CancellationToken ct = default)
        {
            Calls.Add(("watermark", since is null ? "" : $"{since.EntitySeq},{since.ClaimSeq},{since.EvidenceId}"));
            return Task.FromResult(Memory);
        }

        public Task BackfillRangeAsync(IReadOnlyList<ProjectRepositories> projects, MemoryScopeWatermark after, MemoryScopeWatermark upTo, CancellationToken ct = default)
        {
            foreach (var project in projects)
                Calls.Add(($"range:{project.ProjectKey}",
                    $"{string.Join(",", project.Repositories)} {after.EntitySeq},{after.ClaimSeq},{after.EvidenceId}-{upTo.EntitySeq},{upTo.ClaimSeq},{upTo.EvidenceId}"));
            return Task.CompletedTask;
        }
    }
}

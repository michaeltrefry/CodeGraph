using CodeGraph.Models.Responses;
using CodeGraph.Services.Overlays;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

internal static class CommitScopeActionResults
{
    /// <summary>
    /// Maps a commit-pinned request that cannot be answered for its SHA: 400 for an invalid or
    /// ambiguous SHA, 409 when the commit has no ready overlay. The body is always a
    /// <see cref="CommitNotIndexedResponse"/>, never default-branch data.
    /// </summary>
    public static ActionResult NotIndexed(this ControllerBase controller, CommitNotIndexedResponse response)
        => response.Code == CommitScopeResolver.NotIndexedCode
            ? controller.Conflict(response)
            : controller.BadRequest(response);
}

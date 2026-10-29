using System.Text;
using System.Text.Json;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Work;

/// <summary>
/// Fits a briefing into its token budget (E4). Tokens are estimated as UTF-8 bytes of the wire JSON
/// divided by three, which over-counts typical English and JSON, so a briefing within its estimate
/// stays within the budget. Long handoff texts are always cut; when the briefing is still over
/// budget, sections are shortened from the lowest rank up (the suggestion, other sessions, goals,
/// the focus item, then my own work beyond the first item) and named in <c>truncated</c>.
/// Empty sections are left out.
/// </summary>
public static class WorkBriefings
{
    private const int MaxHandoffListEntries = 5;

    public static int Estimate(BriefingView briefing) =>
        (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(briefing, WorkJson.Options)) + 2) / 3;

    public static BriefingView Fit(BriefingView briefing, int budgetTokens)
    {
        var view = briefing with
        {
            BudgetTokens = budgetTokens,
            MyWork = briefing.MyWork?.Select(Shorten).ToList(),
            Focus = briefing.Focus is { } focus ? Shorten(focus) : null,
            SuggestedNext = briefing.SuggestedNext is { } next ? Shorten(next) : null,
            Knowledge = briefing.Knowledge?.Select(row => row with { Summary = CutTo(row.Summary, KnowledgeLimits.BriefingSummaryLength)! }).ToList(),
            Truncated = null
        };

        var truncated = new List<string>();
        foreach (var step in Steps)
        {
            while (Estimate(view with { EstimatedTokens = budgetTokens, Truncated = Names(truncated, step.Section) }) > budgetTokens)
            {
                var shorter = step.Trim(view);
                if (shorter is null)
                    break;
                view = shorter;
                if (!truncated.Contains(step.Section))
                    truncated.Add(step.Section);
            }
        }

        view = view with { Truncated = truncated.Count == 0 ? null : truncated };
        return view with { EstimatedTokens = Estimate(view with { EstimatedTokens = budgetTokens }) };
    }

    private static List<string> Names(List<string> truncated, string next) =>
        truncated.Contains(next) ? truncated : [.. truncated, next];

    private sealed record Step(string Section, Func<BriefingView, BriefingView?> Trim);

    // Lowest rank first. Each step answers a shorter briefing, or null when it has nothing left to cut.
    private static readonly Step[] Steps =
    [
        new("suggested_next", view => view.SuggestedNext switch
        {
            null => null,
            { LatestHandoff: not null } next => view with { SuggestedNext = next with { LatestHandoff = null } },
            _ => view with { SuggestedNext = null }
        }),
        new("other_sessions", view => view.OtherSessions switch
        {
            null => null,
            { Count: 1 } => view with { OtherSessions = null },
            var others => view with { OtherSessions = others.Take(others.Count - 1).ToList() }
        }),
        // Knowledge rows are ranked (item, path, repository, project): the last goes first.
        new("knowledge", view => view.Knowledge switch
        {
            null => null,
            { Count: 1 } => view with { Knowledge = null },
            var rows => view with { Knowledge = rows.Take(rows.Count - 1).ToList() }
        }),
        new("project", view => view.Project.Goals switch
        {
            null => null,
            { Count: 1 } => view with { Project = view.Project with { Goals = null } },
            var goals => view with { Project = view.Project with { Goals = goals.Take(goals.Count - 1).ToList() } }
        }),
        new("focus", view => view.Focus switch
        {
            null => null,
            { LatestHandoff: not null } focus => view with { Focus = focus with { LatestHandoff = null } },
            _ => view with { Focus = null }
        }),
        new("my_work", view =>
        {
            if (view.MyWork is not { Count: > 0 } mine)
                return null;
            if (mine.Count > 1)
                return view with { MyWork = mine.Take(mine.Count - 1).ToList() };
            return mine[0].LatestHandoff is not null ? view with { MyWork = [mine[0] with { LatestHandoff = null }] } : view with { MyWork = null };
        })
    ];

    private static BriefingItem Shorten(BriefingItem item) =>
        item.LatestHandoff is { } handoff ? item with { LatestHandoff = Shorten(handoff) } : item;

    private static HandoffView Shorten(HandoffView handoff) => handoff with
    {
        Done = Cut(handoff.Done),
        NextAction = Cut(handoff.NextAction),
        Decisions = CutList(handoff.Decisions),
        OpenIssues = CutList(handoff.OpenIssues),
        Corrections = CutList(handoff.Corrections)
    };

    private static IReadOnlyList<string>? CutList(IReadOnlyList<string>? values) =>
        values?.Take(MaxHandoffListEntries).Select(value => Cut(value)!).ToList();

    private static string? Cut(string? text) => CutTo(text, WorkSessionLimits.BriefingTextLength);

    private static string? CutTo(string? text, int length) =>
        text is null || text.Length <= length
            ? text
            : text[..(length - 1)] + "…";
}

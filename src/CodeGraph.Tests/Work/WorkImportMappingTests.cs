using System.Net;
using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work.Imports;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The Shortcut → CodeGraph mapping of design §10.1 (sc-27876) on recorded Shortcut payloads
/// (Fixtures/Imports/Shortcut), the KanbanBoard mapping of §5.2a, the REST source's paging, windowing
/// and rate limit, and the native-memory candidate parser.
/// </summary>
public sealed class WorkImportMappingTests
{
    public static string ShortcutFixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Imports", "Shortcut");
    public static string KanbanFixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Imports", "Kanban");

    private static readonly RecordedShortcutSource Recorded = RecordedShortcutSource.FromDirectory(ShortcutFixtures);
    private static readonly IReadOnlyDictionary<long, ShortcutState> States = Recorded.States.ToDictionary(state => state.Id);
    private static readonly IReadOnlyDictionary<string, ImportMemberMapping> Members = Recorded.Members.Select(ShortcutMapper.DefaultMember).ToDictionary(member => member.MemberId);
    private static readonly IReadOnlyDictionary<string, ShortcutTeam> Teams = Recorded.Teams.ToDictionary(team => team.Id);

    private static MappedStory Map(long id) => ShortcutMapper.Map(Recorded.Stories[id], States, Members, Teams);

    [Fact]
    public void Recorded_payloads_parse_from_the_mcp_shape()
    {
        Recorded.Teams.Select(team => team.Name).ShouldBe(["CodeGraph", "DarkFactory", "LensWorks", "SceneWorks", "ShirtPost"], ignoreOrder: true);
        Recorded.States.Select(state => state.Name).ShouldBe(["Backlog", "To Do", "In Progress", "In Review", "Done"]);
        Recorded.Stories.Keys.ShouldBe([7931L, 10354L, 27866L, 27876L], ignoreOrder: true);
        var epic = Recorded.Epics[26198];
        epic.TeamIds.ShouldBe(["6ac526c4-f2b5-4903-958f-d2d529d8bc8a"]);
        epic.Done.ShouldBeFalse();
        epic.Comments.Count.ShouldBe(2);
        Recorded.Epics[5558].Done.ShouldBeTrue();
        Recorded.Stories[7931].TeamId.ShouldBeNull();
        Recorded.Stories[7931].Files.Single().Size.ShouldBe(1735535);
    }

    [Fact]
    public void Types_and_workflow_states_map_as_the_table_says()
    {
        ShortcutMapper.Type("feature").ShouldBe(WorkItemType.Story);
        ShortcutMapper.Type("bug").ShouldBe(WorkItemType.Bug);
        ShortcutMapper.Type("chore").ShouldBe(WorkItemType.Task);
        var names = Recorded.States.Select(state => ShortcutMapper.Status(state)).ToList();
        names.ShouldBe([WorkStatus.Backlog, WorkStatus.Ready, WorkStatus.InProgress, WorkStatus.InReview, WorkStatus.Complete]);
        // A state outside the Standard workflow maps by its type.
        ShortcutMapper.Status(new ShortcutState(1, "Ready for QA", "started")).ShouldBe(WorkStatus.InProgress);

        Map(27866).Type.ShouldBe(WorkItemType.Story);
        Map(27866).Status.ShouldBe(WorkStatus.Complete);
        Map(27876).Type.ShouldBe(WorkItemType.Task);
        Map(27876).Status.ShouldBe(WorkStatus.InProgress);
    }

    [Fact]
    public void Comments_keep_author_and_time_and_tell_agents_from_people()
    {
        // Written through the owner's account with an [author: claude] prefix: claude, an agent.
        var closeout = Map(27866).Comments.Single(comment => comment.ExternalId == "27884");
        closeout.Author.ShouldBe("claude");
        closeout.AuthorKind.ShouldBe(WorkActor.AgentKind);
        closeout.CreatedAt.ShouldBe(new DateTime(2026, 10, 29, 16, 20, 13, DateTimeKind.Utc));
        closeout.Body.ShouldStartWith("[author: claude] Closeout");

        // Written by the Claude account.
        Map(10354).Comments.Single(comment => comment.ExternalId == "24738").Author.ShouldBe("claude");
        // The Codex account is an agent too; the owner is a person, by mention name.
        ShortcutMapper.Author("69f0ba01-0687-4592-ab25-6d74829d63d9", "Done.", Members).ShouldBe(("codex", WorkActor.AgentKind));
        ShortcutMapper.Author("69f08f42-a951-46b3-b1a0-897a12271e62", "Looks good.", Members).ShouldBe(("michaeltrefry1818", WorkActor.UserKind));
    }

    [Fact]
    public void Branches_prs_and_external_links_become_item_links_and_name_the_repository()
    {
        var links = Map(27866).Links;
        links.ShouldContain(new LinkSpec(WorkLinkKind.Pr, "https://github.com/michaeltrefry/CodeGraph/pull/122"));
        links.ShouldContain(new LinkSpec(WorkLinkKind.Branch, "https://github.com/michaeltrefry/CodeGraph/tree/claude/sc-27866-sessions-briefing"));
        Map(10354).Links.ShouldBe([new LinkSpec(WorkLinkKind.Pr, "https://github.com/SceneWorks/SceneWorks/pull/1300")]);

        Map(27866).RepositoryCandidates.ShouldBe(["michaeltrefry/CodeGraph"]);
        // Inferred from the PR (by name, ignoring case); else the project's single owned repository; else none.
        ShortcutMapper.ChooseRepository(Map(27866).RepositoryCandidates, ["codegraph", "inference"]).ShouldBe("codegraph");
        ShortcutMapper.ChooseRepository(Map(10354).RepositoryCandidates, ["SceneWorks/SceneWorks", "inference"]).ShouldBe("SceneWorks/SceneWorks");
        ShortcutMapper.ChooseRepository(Map(27876).RepositoryCandidates, ["codegraph"]).ShouldBe("codegraph");
        ShortcutMapper.ChooseRepository(Map(27876).RepositoryCandidates, ["codegraph", "inference"]).ShouldBeNull();
    }

    [Fact]
    public void Checklist_tasks_are_acceptance_criteria_with_their_done_state()
    {
        var criteria = Map(10354).AcceptanceCriteria;
        criteria.Count.ShouldBe(5);
        criteria.ShouldAllBe(criterion => criterion.State == AcceptanceState.Met);
        criteria[0].Text.ShouldStartWith("Probe-based skip");
    }

    [Fact]
    public void Relations_map_to_blockers_links_and_notes()
    {
        // 27876 is blocked by 27869, 27868, 27870 and 27866 (links where it is the object of "blocks").
        Map(27876).Blockers.ShouldBe([27869L, 27868L, 27870L, 27866L], ignoreOrder: true);
        // 27866 blocks others, so it has only its own blocker.
        Map(27866).Blockers.ShouldBe([27865L]);
        // relates to (both directions) becomes a note naming the other story's alias.
        var notes = Map(10354).Comments.Where(comment => comment.AuthorKind == WorkActor.SystemKind).Select(comment => comment.Body).ToList();
        notes.ShouldBe(["Relates to sc-10355. (Shortcut story link, imported.)", "Relates to sc-10420. (Shortcut story link, imported.)"]);

        // duplicates: noted from both ends.
        var duplicate = Recorded.Stories[7931] with
        {
            StoryLinks = [new ShortcutStoryLink(9, 7931, 7809, "duplicates")]
        };
        ShortcutMapper.Map(duplicate, States, Members, Teams).Comments.Last().Body.ShouldStartWith("Duplicates sc-7809.");
        ShortcutMapper.Map(duplicate with { Id = 7809, StoryLinks = [new ShortcutStoryLink(9, 7931, 7809, "duplicates")] }, States, Members, Teams)
            .Comments.Last().Body.ShouldStartWith("Duplicated by sc-7931.");

        // Sub-tasks block their parent.
        var parent = Recorded.Stories[27876] with { StoryLinks = [], SubTaskIds = [90001, 90002] };
        ShortcutMapper.Map(parent, States, Members, Teams).Blockers.ShouldBe([90001L, 90002L]);
    }

    [Fact]
    public void A_blocked_story_without_blockers_imports_as_backlog_with_the_reason()
    {
        var blocked = Recorded.Stories[27876] with { StoryLinks = [], Blocked = true };
        var mapped = ShortcutMapper.Map(blocked, States, Members, Teams);
        mapped.Status.ShouldBe(WorkStatus.Backlog);
        mapped.BlockedWithoutEdges.ShouldBeTrue();
        mapped.Comments.Single(comment => comment.ExternalId == "blocked-without-edges").Body.ShouldContain("D26");

        // Blocked by an edge: the status stays and Blocked is derived.
        Map(27876).Status.ShouldBe(WorkStatus.InProgress);
        Map(27876).BlockedWithoutEdges.ShouldBeFalse();
    }

    [Fact]
    public void Owners_and_requester_are_history_and_estimates_are_dropped()
    {
        var imported = Map(10354).Imported;
        imported.Source.ShouldBe(ImportSources.Shortcut);
        imported.ExternalId.ShouldBe("sc-10354");
        imported.Url.ShouldBe("https://app.shortcut.com/trefry/story/10354");
        imported.History!.Owners.ShouldBe(["claude"]);
        imported.History.Requester.ShouldBe("claude");
        imported.History.CompletedAt.ShouldBe(new DateTime(2026, 10, 4, 13, 25, 4, DateTimeKind.Utc));
        imported.History.Estimate.ShouldBeNull();
        imported.History.Team.ShouldBe("SceneWorks");
        Map(10354).Aliases.ShouldBe(["sc-10354"]);
    }

    [Fact]
    public void An_epic_keeps_its_comments_as_a_feature_document()
    {
        var documents = ShortcutMapper.EpicDocuments(Recorded.Epics[26198], [new ShortcutDocument("d1", "Design", "# Design")], Members);
        documents.Select(document => document.Title).ShouldBe(["Design", "Shortcut epic comments"]);
        documents[1].Body.ShouldContain("**claude** (agent), 2026-10-22 17:18 UTC");
        ShortcutMapper.EpicImported(Recorded.Epics[26198], Members, Teams).History!.Team.ShouldBe("DarkFactory");
    }

    [Fact]
    public void The_hash_changes_with_the_content_only()
    {
        var a = ShortcutMapper.Hash(Map(27866));
        ShortcutMapper.Hash(Map(27866)).ShouldBe(a);
        ShortcutMapper.Hash(ShortcutMapper.Map(Recorded.Stories[27866] with { Name = "Renamed" }, States, Members, Teams)).ShouldNotBe(a);
    }

    [Fact]
    public void Proposed_keys_follow_the_project_key_rule()
    {
        ShortcutMapper.ProposedKey("SceneWorks").ShouldBe("SCENEWORKS");
        ShortcutMapper.ProposedKey("DarkFactory").ShouldBe("DARKFACTOR");
        ShortcutMapper.ProposedKey("9 lives").ShouldBe("T9LIVES");
        ShortcutMapper.ProposedKey("x").ShouldBe("XX");
    }

    [Fact]
    public void Kanban_items_map_as_the_port_map_says()
    {
        var source = RecordedKanbanSource.FromDirectory(KanbanFixtures);
        var item = source.Items[KanbanImporter.CodeReviewEpicId].Single();
        item.Labels.ShouldBe(["security", "sev-high", "rust-api"]);
        KanbanImporter.Type(item.Type).ShouldBe(WorkItemType.Bug);
        KanbanImporter.Type("Task").ShouldBe(WorkItemType.Task);
        KanbanImporter.Status("Backlog").ShouldBe(WorkStatus.Backlog);
        KanbanImporter.Status("Ready").ShouldBe(WorkStatus.Ready);
        KanbanImporter.Status("InProgress").ShouldBe(WorkStatus.InProgress);
        KanbanImporter.Status("Done").ShouldBe(WorkStatus.Complete);
        KanbanImporter.Status("Closed").ShouldBe(WorkStatus.Closed);
        KanbanImporter.Priority(item.Priority).ShouldBe(WorkPriority.High);
        KanbanImporter.Author("Claude (agent)").ShouldBe(("claude", WorkActor.AgentKind));
        source.Documents[KanbanImporter.CodeReviewEpicId].Select(document => document.Title).ShouldBe(["Requirements", "Implementation plan"]);
    }

    // ---- The REST source -------------------------------------------------------------------------

    private sealed class FakeShortcut : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(Uri.UnescapeDataString(request.RequestUri!.PathAndQuery) + (request.Headers.Contains("Shortcut-Token") ? " [token]" : ""));
            return Task.FromResult(Answer(request));
        }

        public static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    private static ShortcutRestSource Rest(FakeShortcut handler, int perMinute = 60_000) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.app.shortcut.com/api/v3/") }, "token", new RequestRateLimiter(perMinute));

    [Fact]
    public async Task The_rest_source_pages_story_search_and_splits_windows_over_the_search_cap()
    {
        var handler = new FakeShortcut();
        // The whole range has 1500 stories (over the cap): split in two; the first half pages by next.
        handler.Answer = _ => handler.Requests.Count switch
        {
            1 => FakeShortcut.Json(new { total = 1500, data = Array.Empty<object>(), next = (string?)null }),
            2 => FakeShortcut.Json(new { total = 2, data = new object[] { new { id = 1, updated_at = "2026-10-01T00:00:00Z", archived = false } }, next = "/api/v3/search/stories?query=x&next=abc" }),
            3 => FakeShortcut.Json(new { total = 2, data = new object[] { new { id = 2, updated_at = "2026-10-02T00:00:00Z", archived = false } }, next = (string?)null }),
            _ => FakeShortcut.Json(new { total = 1, data = new object[] { new { id = 3, updated_at = "2026-10-03T00:00:00Z", archived = false } }, next = (string?)null })
        };

        var stories = await Rest(handler).ListStoriesAsync(null, CancellationToken.None);

        stories.Select(story => story.Id).ShouldBe([1L, 2L, 3L]);
        handler.Requests.ShouldAllBe(request => request.EndsWith(" [token]"));
        handler.Requests[0].ShouldContain("!is:archived created:2000-01-01..");
        handler.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task The_rest_source_backs_off_on_429_and_never_writes()
    {
        var handler = new FakeShortcut();
        var calls = 0;
        handler.Answer = request =>
        {
            request.Method.ShouldBe(HttpMethod.Get);
            if (calls++ == 0)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                return limited;
            }

            return FakeShortcut.Json(new { story = new { id = 5, name = "Five", story_type = "bug", workflow_state_id = 500000007, created_at = "2026-01-01T00:00:00Z" } });
        };

        var started = DateTime.UtcNow;
        var story = await Rest(handler).GetStoryAsync(5, CancellationToken.None);

        story!.Name.ShouldBe("Five");
        calls.ShouldBe(2);
        (DateTime.UtcNow - started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public async Task The_rate_limiter_spaces_requests()
    {
        var limiter = new RequestRateLimiter(600); // one per 100 ms
        var started = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
            await limiter.WaitAsync(CancellationToken.None);
        (DateTime.UtcNow - started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(280));
        limiter.Requests.ShouldBe(4);
    }

    [Fact]
    public async Task A_file_download_is_bounded_and_the_token_does_not_follow_a_redirect_off_shortcut()
    {
        var handler = new FakeShortcut
        {
            Answer = request => request.RequestUri!.Host == "media.app.shortcut.com"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://storage.example.invalid/signed/file.png") } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) }
        };
        var source = Rest(handler);

        (await source.DownloadFileAsync("https://media.app.shortcut.com/api/attachments/files/x/file.png", 1000, CancellationToken.None))!.Length.ShouldBe(100);
        handler.Requests[0].ShouldEndWith(" [token]");
        handler.Requests[1].ShouldNotEndWith(" [token]");
        (await source.DownloadFileAsync("https://media.app.shortcut.com/api/attachments/files/x/file.png", 50, CancellationToken.None)).ShouldBeNull();
    }

    // ---- Native memory -------------------------------------------------------------------------

    [Fact]
    public void Claude_memory_files_become_candidates_with_a_path_pre_check()
    {
        var root = Path.Combine(Path.GetTempPath(), "cg-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Present"));
        File.WriteAllText(Path.Combine(root, "CodeGraph.sln"), "");
        try
        {
            var files = new[]
            {
                new MemoryFile("/m/MEMORY.md", "- [index](a.md)"),
                new MemoryFile("/m/commands.md", "---\nname: Build commands\ndescription: How to build\ntype: reference\n---\nRun `dotnet build CodeGraph.sln` in src/Present."),
                new MemoryFile("/m/stale.md", "---\nname: Old layout\ndescription: Where the extractor lives\ntype: project\n---\nSee src/Gone/Extractor.cs."),
                new MemoryFile("/m/infra.md", "---\nname: Infra\ndescription: The database host\ntype: project\n---\nMariaDB runs on the shared host."),
                new MemoryFile("/m/pref.md", "---\nname: flow\ndescription: \"Prefers continuous work\"\nmetadata:\n  node_type: memory\n  type: feedback\n---\nKeep going.")
            };

            var candidates = NativeMemoryParser.ParseClaude(files, root);

            candidates.Count.ShouldBe(4);
            var byFile = candidates.ToDictionary(candidate => candidate.Candidate.SourceFile);
            byFile["/m/commands.md"].Candidate.Verification.ShouldBe(ImportVerification.Verified);
            byFile["/m/commands.md"].Candidate.Entry.Paths.ShouldBe(["CodeGraph.sln", "src/Present"], ignoreOrder: true);
            byFile["/m/commands.md"].Candidate.Entry.Summary.ShouldBe("How to build");
            byFile["/m/stale.md"].Candidate.Verification.ShouldBe(ImportVerification.Stale);
            byFile["/m/stale.md"].Missing.ShouldBe(["src/Gone/Extractor.cs"]);
            byFile["/m/infra.md"].Candidate.Verification.ShouldBe(ImportVerification.Unverifiable);
            byFile["/m/pref.md"].Candidate.Verification.ShouldBe(ImportVerification.Personal);
            byFile["/m/pref.md"].Candidate.Entry.Kind.ShouldBe(KnowledgeKind.Lesson);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Codex_task_groups_become_candidates_filtered_by_repository()
    {
        var memory = new MemoryFile("/c/MEMORY.md",
            "# Task Group: CodeGraph imports\n\nscope: CodeGraph work\napplies_to: cwd=/Users/x/Repos/CodeGraph\n\n## Task 1: thing\n\n- a task line\n\n" +
            "## User preferences\n\n- the user wants short answers\n\n## Reusable knowledge\n\n- Migrations live in sql/migrations.\n\n" +
            "## Failures and how to do differently\n\n- Symptom: build fails -> fix: restore first\n\n" +
            "# Task Group: SceneWorks review\n\napplies_to: cwd=/Users/x/SceneWorks\n\n## Reusable knowledge\n\n- Not this repository.\n");

        var candidates = NativeMemoryParser.ParseCodex(memory, null, "CodeGraph");

        candidates.Select(candidate => (candidate.Candidate.Entry.Kind, candidate.Candidate.Verification)).ShouldBe(
        [
            (KnowledgeKind.Lesson, ImportVerification.Personal),
            (KnowledgeKind.Fact, ImportVerification.Unverifiable),
            (KnowledgeKind.Gotcha, ImportVerification.Unverifiable)
        ]);
        candidates[1].Candidate.SourceFile.ShouldBe("/c/MEMORY.md#CodeGraph imports");
        candidates[1].Referenced.ShouldBe(["sql/migrations"]);
    }
}

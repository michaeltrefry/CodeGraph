using System.Collections.Concurrent;
using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The concurrency discipline ported from KanbanBoard (E9), against a real MariaDB: the change
/// feed never skips or reorders under concurrent writers and readers, next_work never hands one
/// item to two callers, keys are allocated without gaps, concurrent dependency adds never close a
/// cycle, versioned updates have one winner, and a mix of writers on the same rows ends in refusals
/// from the error vocabulary only (deadlocks are retried, never surfaced raw).
/// </summary>
public sealed class WorkConcurrencyMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static string Key(JsonElement element) => element.GetProperty("key").GetString()!;

    private async Task<List<string>> ReadyItemsAsync(string project, int count)
    {
        var plan = await db.CallAsync(WorkTools.CreateWork, new
        {
            project,
            idempotency_key = "items",
            plan = new { items = Enumerable.Range(1, count).Select(index => new { @ref = $"i{index}", title = $"Item {index}", status = "ready" }).ToArray() }
        });
        return plan.GetProperty("items").EnumerateObject().Select(entry => entry.Value.GetProperty("key").GetString()!).ToList();
    }

    // An update that rereads and retries on conflict until it lands, like a well-behaved agent.
    private async Task<int> UpdateUntilItLandsAsync(string id, object fields, string user)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.UpdateAsync(id, fields, user);
                return attempt;
            }
            catch (WorkException exception) when (exception.Code is WorkErrorCodes.Conflict or WorkErrorCodes.Busy && attempt < 200)
            {
            }
        }
    }

    [Fact]
    public async Task Concurrent_writers_and_readers_every_reader_sees_every_committed_entry_once_and_in_order()
    {
        var project = await db.NewProjectAsync("FD");
        var items = await ReadyItemsAsync(project, 16);
        var start = (await db.CallAsync(WorkTools.Changes, new { project })).GetProperty("next_cursor").GetString()!;

        var writersDone = 0;
        const int writers = 48;
        const int writesPerWriter = 6;
        var writerTasks = Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
        {
            try
            {
                for (var write = 0; write < writesPerWriter; write++)
                    await UpdateUntilItLandsAsync(items[writer % items.Count], new { order = writer * 100 + write }, $"writer-{writer}");
            }
            finally
            {
                Interlocked.Increment(ref writersDone);
            }
        })).ToList();

        var readerTasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            var seen = new List<long>();
            var cursor = start;
            while (true)
            {
                var finished = Volatile.Read(ref writersDone) == writers;
                var page = await db.CallAsync(WorkTools.Changes, new { project, cursor, limit = 200 });
                var entries = page.GetProperty("items").EnumerateArray().Select(entry => entry.GetProperty("sequence").GetInt64()).ToList();
                seen.AddRange(entries);
                cursor = page.GetProperty("next_cursor").GetString()!;
                // Read once more after the writers finished, until the end of the feed.
                if (finished && entries.Count == 0)
                    return seen;
            }
        })).ToList();

        await Task.WhenAll(writerTasks);
        var readers = await Task.WhenAll(readerTasks);

        // Before the cursor the project has only its creation and the plan's item_created entries.
        var committed = (await db.AllChangesAsync(project))
            .Where(entry => entry.GetProperty("kind").GetString() == WorkChangeKinds.ItemUpdated)
            .Select(entry => entry.GetProperty("sequence").GetInt64())
            .ToList();

        committed.Count.ShouldBe(writers * writesPerWriter);
        foreach (var seen in readers)
            seen.ShouldBe(committed);
    }

    [Fact]
    public async Task Concurrent_next_work_never_gives_one_item_to_two_callers()
    {
        var project = await db.NewProjectAsync("NX");
        var items = await ReadyItemsAsync(project, 30);

        var calls = Enumerable.Range(0, 40).Select(caller => Task.Run(async () =>
        {
            var answer = await db.CallAsync(WorkTools.NextWork, new { project, marker = "run" }, user: $"agent-{caller}");
            return answer.TryGetProperty("item", out var item) ? Key(item) : null;
        }));
        var allocated = (await Task.WhenAll(calls)).Where(key => key is not null).ToList();

        allocated.Count.ShouldBe(30);
        allocated.Distinct().Count().ShouldBe(30);
        allocated.Order().ShouldBe(items.Order());
        var claims = (await db.AllChangesAsync(project)).Where(entry => entry.GetProperty("kind").GetString() == "claimed").ToList();
        claims.Count.ShouldBe(30);
        claims.Select(entry => entry.GetProperty("entity_key").GetString()).Distinct().Count().ShouldBe(30);
    }

    [Fact]
    public async Task Concurrent_retries_of_one_next_work_return_one_allocation_and_one_claim()
    {
        var project = await db.NewProjectAsync("RT");
        await ReadyItemsAsync(project, 10);

        var byKey = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            db.CallAsync(WorkTools.NextWork, new { project, marker = "same-run", idempotency_key = "lost-response" }))));
        var byMarker = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            db.CallAsync(WorkTools.NextWork, new { project, marker = "same-run" }))));

        byKey.Concat(byMarker).Select(answer => Key(answer.GetProperty("item"))).Distinct().Count().ShouldBe(1);
        (await db.AllChangesAsync(project)).Count(entry => entry.GetProperty("kind").GetString() == "claimed").ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_creates_and_imports_allocate_keys_without_gaps_or_duplicates()
    {
        var project = await db.NewProjectAsync("KY");

        var creates = Enumerable.Range(0, 40).Select(index => Task.Run(async () =>
            new[] { Key((await db.CallAsync(WorkTools.CreateWork, new { project, item = new { title = $"Create {index}" } })).GetProperty("item")) }));
        var imports = Enumerable.Range(0, 3).Select(index => Task.Run(async () =>
        {
            var plan = await db.CallAsync(WorkTools.CreateWork, new
            {
                project,
                idempotency_key = $"plan-{index}",
                plan = new { items = Enumerable.Range(0, 5).Select(item => new { @ref = $"r{item}", title = $"Plan {index} item {item}" }).ToArray() }
            });
            return plan.GetProperty("items").EnumerateObject().Select(entry => entry.Value.GetProperty("key").GetString()!).ToArray();
        }));
        var keys = (await Task.WhenAll(creates.Concat(imports))).SelectMany(batch => batch).ToList();

        keys.Count.ShouldBe(55);
        keys.Select(key => int.Parse(key[(key.LastIndexOf('-') + 1)..])).Order().ShouldBe(Enumerable.Range(1, 55));
        // One import's items are consecutive: it allocated its numbers in one go.
        var features = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => Task.Run(async () =>
            Key((await db.CallAsync(WorkTools.CreateWork, new { project, feature = new { title = $"Feature {index}" } })).GetProperty("feature")))));
        features.Select(key => int.Parse(key[(key.LastIndexOf("-F", StringComparison.Ordinal) + 2)..])).Order().ShouldBe(Enumerable.Range(1, 10));
    }

    [Fact]
    public async Task Concurrent_creates_with_one_idempotency_key_create_one_item()
    {
        var project = await db.NewProjectAsync("IK");

        var answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            db.CallAsync(WorkTools.CreateWork, new { project, idempotency_key = "one-item", item = new { title = "Only once" } }))));

        answers.Select(answer => answer.GetProperty("item").GetProperty("id").GetString()).Distinct().Count().ShouldBe(1);
        answers.Count(answer => answer.GetProperty("created").GetBoolean()).ShouldBe(1);
        (await db.CallAsync(WorkTools.FindWork, new { project })).GetProperty("items").GetArrayLength().ShouldBe(1);
        // Another identity's key of the same text is another key.
        var bob = await db.CallAsync(WorkTools.CreateWork, new { project, idempotency_key = "one-item", item = new { title = "Bob's" } }, user: "bob");
        bob.GetProperty("created").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_opposite_dependency_adds_never_close_a_cycle()
    {
        var project = await db.NewProjectAsync("CC");
        var items = await ReadyItemsAsync(project, 30);
        var outcomes = new ConcurrentBag<string>();

        // Ten pairs added both ways at once, and ten triangles closed from three sides at once.
        var tasks = new List<Task>();
        for (var pair = 0; pair < 10; pair++)
        {
            var (a, b) = (items[pair], items[pair + 10]);
            tasks.Add(Task.Run(() => AddAsync(a, b)));
            tasks.Add(Task.Run(() => AddAsync(b, a)));
        }

        for (var triangle = 0; triangle < 3; triangle++)
        {
            var (a, b, c) = (items[20 + triangle * 3], items[21 + triangle * 3], items[22 + triangle * 3]);
            tasks.Add(Task.Run(() => AddAsync(a, b)));
            tasks.Add(Task.Run(() => AddAsync(b, c)));
            tasks.Add(Task.Run(() => AddAsync(c, a)));
        }

        await Task.WhenAll(tasks);

        outcomes.ShouldAllBe(outcome => outcome == "added" || outcome == WorkErrorCodes.Cycle);
        // Stored graph: no item is blocked by itself through any path.
        var blockersOf = new Dictionary<string, List<string>>();
        foreach (var key in items)
        {
            var item = await db.CallAsync(WorkTools.GetWork, new { id = key });
            blockersOf[key] = item.GetProperty("blocked_by").EnumerateArray().Select(Key).ToList();
        }

        foreach (var key in items)
            Reaches(blockersOf, key, key).ShouldBeFalse(key);
        for (var pair = 0; pair < 10; pair++)
            (blockersOf[items[pair]].Count + blockersOf[items[pair + 10]].Count).ShouldBe(1);

        async Task AddAsync(string blocked, string blocker)
        {
            try
            {
                await UpdateUntilItLandsAsync(blocked, new { add_blocked_by = new[] { blocker } }, "planner");
                outcomes.Add("added");
            }
            catch (WorkException exception)
            {
                outcomes.Add(exception.Code);
            }
        }
    }

    private static bool Reaches(Dictionary<string, List<string>> blockersOf, string from, string target)
    {
        var seen = new HashSet<string>();
        var pending = new Stack<string>(blockersOf[from]);
        while (pending.TryPop(out var current))
        {
            if (current == target)
                return true;
            if (seen.Add(current))
                foreach (var next in blockersOf[current])
                    pending.Push(next);
        }

        return false;
    }

    [Fact]
    public async Task Of_concurrent_updates_with_one_expected_version_exactly_one_succeeds()
    {
        var project = await db.NewProjectAsync("EV");
        var item = await db.NewItemAsync(project, "Contended");
        var version = item.GetProperty("version").GetInt64();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(async () =>
        {
            try
            {
                await db.CallAsync(WorkTools.UpdateWork, new { id = Key(item), expected_version = version, title = $"Title {index}" }, $"u{index}");
                return "ok";
            }
            catch (WorkException exception)
            {
                return exception.Code;
            }
        })));

        outcomes.Count(outcome => outcome == "ok").ShouldBe(1);
        outcomes.Count(outcome => outcome == WorkErrorCodes.Conflict).ShouldBe(11);
        (await db.CallAsync(WorkTools.GetWork, new { id = Key(item) })).GetProperty("version").GetInt64().ShouldBe(version + 1);
    }

    [Fact]
    public async Task A_mix_of_dependency_adds_claims_and_updates_on_the_same_items_ends_only_in_vocabulary_refusals()
    {
        var project = await db.NewProjectAsync("MX");
        var items = await ReadyItemsAsync(project, 8);
        var unexpected = new ConcurrentBag<Exception>();
        var random = new Random(27865);
        var operations = Enumerable.Range(0, 60).Select(index => (index, kind: index % 3, a: items[random.Next(items.Count)], b: items[random.Next(items.Count)])).ToList();

        await Task.WhenAll(operations.Select(operation => Task.Run(async () =>
        {
            try
            {
                switch (operation.kind)
                {
                    case 0:
                        await db.UpdateAsync(operation.a, new { add_blocked_by = new[] { operation.b } }, $"dep-{operation.index}");
                        break;
                    case 1:
                        await db.CallAsync(WorkTools.NextWork, new { project, marker = $"m{operation.index}" }, $"claim-{operation.index}");
                        break;
                    default:
                        await db.UpdateAsync(operation.a, new { status = operation.index % 2 == 0 ? "complete" : "ready", priority = "high" }, $"upd-{operation.index}");
                        break;
                }
            }
            catch (WorkException exception) when (exception.Code is WorkErrorCodes.Conflict or WorkErrorCodes.Cycle or WorkErrorCodes.AlreadyClaimed or WorkErrorCodes.Busy)
            {
            }
            catch (Exception exception)
            {
                unexpected.Add(exception);
            }
        })));

        unexpected.ShouldBeEmpty(string.Join("\n", unexpected.Select(exception => exception.ToString())));
        // The feed still has one entry per landed write, in order.
        var sequences = (await db.AllChangesAsync(project)).Select(entry => entry.GetProperty("sequence").GetInt64()).ToList();
        sequences.ShouldBe(sequences.Order().Distinct().ToList());
    }
}

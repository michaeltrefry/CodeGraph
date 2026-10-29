using System.Globalization;
using System.Text.Json.Nodes;

namespace CodeGraph.Cg;

/// <summary>
/// Renders API answers as short lines of data: labels and values, no instructions. A section the
/// answer leaves out is not printed.
/// </summary>
public static class CgRender
{
    public static IEnumerable<string> Briefing(JsonNode? briefing)
    {
        if (briefing is null)
            yield break;

        var project = briefing["project"];
        yield return $"codegraph session {Str(briefing["session_id"])} · project {Str(project?["key"])} ({Str(project?["name"])})";
        if (project?["goals"] is JsonArray goals)
            foreach (var goal in goals)
                yield return $"goal: {Str(goal)}";

        if (briefing["my_work"] is JsonArray mine)
            foreach (var line in Items("my work", mine))
                yield return line;

        if (briefing["focus"] is JsonObject focus)
            foreach (var line in Item("focus", focus))
                yield return line;

        if (briefing["other_sessions"] is JsonArray others)
        {
            foreach (var other in others)
            {
                var where = Join("/", Str(other?["repository"]), Str(other?["branch"]));
                var holds = other?["holds"] is JsonArray held
                    ? " · holds " + string.Join(", ", held.Select(hold => $"{Str(hold?["key"])} [{Str(hold?["status"])}] {Str(hold?["title"])}"))
                    : "";
                var intent = other?["intent"] is { } text ? $" · intent: {Str(text)}" : "";
                yield return $"other session: {Str(other?["identity"])} {Join(" ", Str(other?["harness"]), Str(other?["model"]))}" +
                    $"{(other?["host"] is { } host ? $" on {Str(host)}" : "")}{(where.Length > 0 ? $" · {where}" : "")} · seen {Str(other?["last_seen_at"])}{holds}{intent}";
            }
        }

        if (briefing["suggested_next"] is JsonObject next)
            foreach (var line in Item("suggested next", next))
                yield return line;

        if (briefing["inbox"] is JsonObject inbox)
            foreach (var line in BriefingInbox(inbox))
                yield return line;

        if (briefing["my_holds"] is JsonArray myHolds)
            foreach (var hold in myHolds.OfType<JsonObject>())
                yield return "my hold: " + HoldLine(hold, withResource: true);

        if (briefing["overlap_warnings"] is JsonArray overlaps)
            foreach (var warning in overlaps.OfType<JsonObject>())
                yield return $"overlap warning ({Str(warning["kind"])}): {Str(warning["message"])}";

        if (briefing["resources"] is JsonArray resources)
            foreach (var line in Resources(resources))
                yield return line;

        // Rules (S9): one line each, with what they conflict with and how often they are challenged.
        if (briefing["rules"] is JsonArray rules)
            foreach (var rule in rules.OfType<JsonObject>())
            {
                var conflicts = rule["contradicts"] is JsonArray links ? " · contradicts: " + string.Join("; ", links.Select(link => Str(link?["summary"]))) : "";
                var challenged = rule["challenges"] is JsonArray open ? $" · challenged {open.Count + (rule["more_challenges"]?.GetValue<int>() ?? 0)}x" : "";
                yield return $"rule ({Str(rule["priority"])}, {string.Join(",", (rule["activity"] as JsonArray ?? []).Select(Str))}): {Str(rule["summary"])}{conflicts}{challenged}";
            }

        // Sections of later stories (S5, S7, S8) print as they appear; unknown ones as their JSON.
        foreach (var (name, value) in briefing.AsObject())
        {
            if (name is "session_id" or "budget_tokens" or "estimated_tokens" or "project" or "my_work" or "focus" or "other_sessions" or "suggested_next" or "inbox" or "truncated" or "my_holds" or "resources" or "overlap_warnings" or "rules" || value is null)
                continue;
            yield return $"{name.Replace('_', ' ')}: {value.ToJsonString()}";
        }

        if (briefing["truncated"] is JsonArray truncated)
            yield return $"truncated to {Str(briefing["budget_tokens"])} tokens: {string.Join(", ", truncated.Select(Str))}";
    }

    /// <summary>One line for the answer of cg hold renew / release.</summary>
    public static IEnumerable<string> Hold(string action, JsonNode? answer)
    {
        var hold = (answer?["hold"] ?? answer) as JsonObject;
        if (hold is null)
            yield break;
        yield return $"codegraph: hold {Str(hold["id"])} {(action == "renew" ? "renewed" : "released")}: {HoldLine(hold, withResource: true)}";
    }

    private static IEnumerable<string> Resources(JsonArray resources)
    {
        foreach (var resource in resources.OfType<JsonObject>())
        {
            var holders = resource["holders"] is JsonArray held ? held.OfType<JsonObject>().Select(hold => HoldLine(hold, withResource: false)).ToList() : [];
            var waiters = resource["waiters"] is JsonArray waiting ? waiting.OfType<JsonObject>().Select(hold => HoldLine(hold, withResource: false)).ToList() : [];
            var free = resource["available"]?.GetValue<bool>() == true ? " · available" : "";
            yield return $"resource {Str(resource["name"])} [{Str(resource["used"])}/{Str(resource["capacity"])}]{free}";
            foreach (var holder in holders)
                yield return "  " + holder;
            foreach (var waiter in waiters)
                yield return "  " + waiter;
        }
    }

    // One hold as a line: held (or waiting #n) by whom, for what, until when; a silent owner is said.
    private static string HoldLine(JsonObject hold, bool withResource)
    {
        var holder = hold["holder"] as JsonObject;
        var who = Join(" ", Str(holder?["identity"]), Str(holder?["harness"]), Str(holder?["model"]));
        var state = Str(hold["state"]) switch
        {
            "waiting" => $"waiting #{Str(hold["queue_position"])}",
            "held" when hold["owner_unreachable"]?.GetValue<bool>() == true => $"held — owner unreachable since {Str(hold["owner_unreachable_since"])}",
            var other => other
        };
        var until = hold["state"]?.GetValue<string>() == "held" && hold["expires_at"] is { } expires ? $" until {Str(expires)}" : "";
        var item = hold["item"] is { } key ? $" · item {Str(key)}" : "";
        return $"{(withResource ? Str(hold["resource"]) + " " : "")}{state}{until} · {who} · {Str(hold["purpose"])}{item}";
    }


    // The briefing's inbox section: how many wait, and one line each for the first few.
    private static IEnumerable<string> BriefingInbox(JsonObject inbox)
    {
        yield return $"inbox: {Str(inbox["unread"])} unread";
        if (inbox["items"] is JsonArray items)
            foreach (var line in items.OfType<JsonObject>())
                yield return $"  {MessageHead(line)}: {Str(line["summary"])}";
    }

    // The inbox tool's answer: a head line and the whole text of each message.
    public static IEnumerable<string> Inbox(JsonNode? inbox)
    {
        if (inbox?["items"] is not JsonArray { Count: > 0 } items)
            yield break;

        yield return $"inbox: {items.Count} message{(items.Count == 1 ? "" : "s")}";
        foreach (var message in items.OfType<JsonObject>())
        {
            var from = message["from"] as JsonObject;
            var sender = Join(" ", Str(from?["name"]), from?["harness"] is { } harness ? $"({Str(harness)})" : "");
            var state = message["state"] is { } open ? $", {Str(open)}" : "";
            var item = message["item"] is { } key ? $" about {Str(key)}" : "";
            var handed = message["handed_over"]?.GetValue<bool>() == true ? " (handed over)" : "";
            var blocking = message["blocking"]?.GetValue<bool>() == true ? " (blocks the item)" : "";
            yield return $"message {Str(message["id"])} [{Str(message["kind"])}{state}] from {sender}{item}{handed}{blocking}: {Str(message["body"])}";
        }
    }

    private static string MessageHead(JsonObject line)
    {
        var state = line["state"] is { } open ? $", {Str(open)}" : "";
        var item = line["item"] is { } key ? $" about {Str(key)}" : "";
        var handed = line["handed_over"]?.GetValue<bool>() == true ? " (handed over)" : "";
        return $"message {Str(line["id"])} [{Str(line["kind"])}{state}] from {Str(line["from"])}{item}{handed}";
    }

    public static IEnumerable<string> Ended(JsonNode? ended)
    {
        var released = ended?["released"] is JsonArray items && items.Count > 0
            ? "; released " + string.Join(", ", items.Select(item => Str(item?["key"])))
            : "";
        var handoff = ended?["handoff"] is JsonObject ? "; handoff recorded" : "";
        var holds = ended?["released_holds"] is JsonArray freed && freed.Count > 0
            ? "; released holds " + string.Join(", ", freed.Select(hold => Str(hold?["resource"])))
            : "";
        yield return $"codegraph session {Str(ended?["session"]?["id"])} ended{released}{holds}{handoff}";
    }

    private static IEnumerable<string> Items(string label, JsonArray items)
    {
        foreach (var item in items.OfType<JsonObject>())
            foreach (var line in Item(label, item))
                yield return line;
    }

    private static IEnumerable<string> Item(string label, JsonObject item)
    {
        var recoverable = item["recoverable"]?.GetValue<bool>() == true ? " · recoverable" : "";
        var previous = item["previous_holder"] is JsonObject holder
            ? $" · previous holder {Join(" ", Str(holder["identity"]), Str(holder["harness"]))}"
            : "";
        var links = new List<string>();
        if (item["branches"] is JsonArray branches)
            links.AddRange(branches.Select(branch => $"branch {Str(branch)}"));
        if (item["prs"] is JsonArray prs)
            links.AddRange(prs.Select(pr => $"pr {Str(pr)}"));
        yield return $"{label}: {Str(item["key"])} [{Str(item["status"])}] {Str(item["title"])}{recoverable}{previous}" +
            (links.Count > 0 ? " · " + string.Join(" · ", links) : "");

        if (item["latest_handoff"] is JsonObject handoff)
        {
            var parts = new List<string>();
            Add(parts, "done", handoff["done"]);
            Add(parts, "next action", handoff["next_action"]);
            Add(parts, "open issues", handoff["open_issues"]);
            Add(parts, "decisions", handoff["decisions"]);
            Add(parts, "corrections", handoff["corrections"]);
            Add(parts, "branch", handoff["branch"]);
            Add(parts, "commit", handoff["commit"]);
            yield return $"  last handoff ({Join(" ", Str(handoff["author"]), Str(handoff["harness"]))}, {Str(handoff["created_at"])}): {string.Join(" | ", parts)}";
        }
    }

    private static void Add(List<string> parts, string label, JsonNode? value)
    {
        if (value is JsonArray list)
            parts.Add($"{label}: {string.Join("; ", list.Select(Str))}");
        else if (value is not null)
            parts.Add($"{label}: {Str(value)}");
    }

    private static string Join(string separator, params string[] parts) =>
        string.Join(separator, parts.Where(part => part.Length > 0));

    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var text) => CgApp.OneLine(text),
        JsonValue value => CgApp.OneLine(Convert.ToString(value.GetValue<object>(), CultureInfo.InvariantCulture) ?? ""),
        _ => node.ToJsonString()
    };
}

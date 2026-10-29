Recorded Shortcut payloads for the importer tests (sc-27876), captured read-only on 2026-10-29 with the
Shortcut MCP tools (`teams-list`, `users-list`, `epics-get-by-id`, `stories-get-by-id`). They are the
REST v3 objects as the MCP returns them (`team_id` where REST says `group_id`; the importer reads both).

Edits: epics 27864, 5558 and 7840 are the slim epic objects the story reads returned (relatedEntities)
with created/updated times filled in; one link (500008146, 27866 blocks 27876) is added to both stories so
a `blocks` relation has both ends inside the set.

Scrubbed: e-mail addresses and one member's name are placeholders; long descriptions and comment texts are shortened (marked
with an ellipsis); `files/7942` holds placeholder bytes, not the real image. No tokens.

Cases covered by the real payloads: an epic with E-requirements and acceptance tests whose comments are
written through the owner's account with an `[author: claude]` prefix (26198); stories of type feature
and chore in In Progress and Done; `blocks` links in both directions (27866, 27876); `relates to` links
in both directions (10354, 7931); a checklist with completed tasks (10354); a branch and a merged PR
(27866); an external PR link (10354); a story without a team carrying a file attachment and a comment by
the Claude account (7931). Cases the tests derive from these (bug, Backlog / To Do / In Review, sub-tasks,
duplicates, a Codex comment, an archived story, a blocked story without blockers) are built in the tests.

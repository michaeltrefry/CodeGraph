-- CG-174: no rule waits for the owner's approval and no autonomous decision waits for acknowledgment
-- (owner decision 2026-11-03; DevWorks KNOW-03, KNOW-05, REVIEW-01). A correction from the human's
-- prompt or review, direct (prompt, review) or relayed by an agent with the owner's words
-- (relayed_prompt, relayed_review), is a rule at once; a lesson an agent, subagent or automated
-- reviewer inferred becomes a rule once recorded from Work:Lessons:RuleRecurrenceThreshold distinct
-- sources (sessions, or identities without a session). The review queue no longer lists proposed
-- lessons or autonomous decisions; that is a read change and needs no data change here.

-- The owner's words as the agent relayed them, per correction, so a paraphrase can be checked.
ALTER TABLE lesson_events ADD COLUMN IF NOT EXISTS source_quote MEDIUMTEXT NULL AFTER source_ref_key;

-- Lessons carrying a person's correction (their own source, or a counted correction merged into them)
-- that were left waiting, as proposed relayed lessons or as candidates from a direct review, become
-- rules now. A rule needs an applies-when activity; a rejected lesson stays rejected, as in the store
-- (only a new correction from a person overrides a rejection). Re-running changes nothing: the rows it
-- would change are rules after the first run.
UPDATE knowledge_entries e
SET e.rule_reason = CASE
        WHEN e.source_kind IN ('prompt', 'relayed_prompt')
          OR EXISTS (SELECT 1 FROM lesson_events le WHERE le.entry_id = e.id AND le.counted = 1 AND le.source_kind IN ('prompt', 'relayed_prompt'))
        THEN 'human_prompt' ELSE 'human_review' END,
    e.rule_state = 'rule',
    e.rule_since = UTC_TIMESTAMP(6),
    e.rejected_reason = NULL
WHERE e.kind = 'lesson' AND e.status = 'active' AND e.rule_state IN ('candidate', 'proposed')
  AND e.activities IS NOT NULL AND e.activities <> ''
  AND (e.source_kind IN ('prompt', 'review', 'relayed_prompt', 'relayed_review')
       OR EXISTS (SELECT 1 FROM lesson_events le WHERE le.entry_id = e.id AND le.counted = 1
                  AND le.source_kind IN ('prompt', 'review', 'relayed_prompt', 'relayed_review')));

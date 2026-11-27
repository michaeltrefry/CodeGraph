-- The reply that answered or declined a question or request, so its asker and the humans can read the answer (CG-180).
ALTER TABLE work_messages ADD COLUMN IF NOT EXISTS resolution_message_id CHAR(36) NULL;

-- Link questions answered before this migration to the reply that closed them: posted by the resolver, at the resolution's instant.
UPDATE work_messages q JOIN work_messages r ON r.in_reply_to = q.id AND r.created_at = q.resolved_at AND r.sender_name = q.resolved_by
SET q.resolution_message_id = r.id
WHERE q.resolution_message_id IS NULL AND q.state IN ('answered', 'declined');

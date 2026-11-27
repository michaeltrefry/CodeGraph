-- The reply that answered or declined a question or request, so its asker and the humans can read the answer (CG-180).
ALTER TABLE work_messages ADD COLUMN IF NOT EXISTS resolution_message_id CHAR(36) NULL;

-- Keep the agent's attribution and identify a resolution relayed from chat.
ALTER TABLE work_messages ADD COLUMN IF NOT EXISTS resolution_relayed_from_chat BOOLEAN NOT NULL DEFAULT FALSE;

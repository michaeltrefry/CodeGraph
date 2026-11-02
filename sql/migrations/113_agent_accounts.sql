-- Agent accounts (CG-F16): named non-human identities the owner manages in Admin, each with its own
-- MCP personal access tokens, so a write made by an AI harness is attributed to that agent (actor
-- kind agent) instead of to the person who would otherwise own the token.
--
-- An agent is never hard-deleted: deleting it stamps deleted_at (and revokes its tokens), so the
-- tokens keep pointing at a row. A deleted agent's name may be used again: uniqueness is on
-- active_name, which is NULL once the agent is deleted. Existing rows keep their recorded author.

CREATE TABLE IF NOT EXISTS agents (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    name VARCHAR(64) NOT NULL,
    created_by VARCHAR(255) NOT NULL,
    created_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL,
    disabled_at DATETIME(3) NULL,
    deleted_at DATETIME(3) NULL,
    active_name VARCHAR(64) AS (IF(deleted_at IS NULL, name, NULL)) PERSISTENT,
    UNIQUE KEY uq_agents_active_name (active_name)
) ENGINE=InnoDB;

-- The agent a token belongs to; NULL for a person's own token (username is then the owner).
ALTER TABLE mcp_personal_access_tokens ADD COLUMN IF NOT EXISTS agent_id BIGINT NULL AFTER username;
CREATE INDEX IF NOT EXISTS ix_mcp_personal_access_tokens_agent ON mcp_personal_access_tokens (agent_id);

-- Whether a session's identity is a person (user) or an agent: a session is used only by the
-- identity, of the same kind, that started it, and an agent's session writes keep kind agent.
ALTER TABLE work_sessions ADD COLUMN IF NOT EXISTS identity_kind VARCHAR(16) NOT NULL DEFAULT 'user' AFTER identity;

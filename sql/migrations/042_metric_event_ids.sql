ALTER TABLE llm_usage
    ADD COLUMN event_id VARCHAR(32) NULL AFTER id;

UPDATE llm_usage
SET event_id = REPLACE(UUID(), '-', '')
WHERE event_id IS NULL OR event_id = '';

ALTER TABLE llm_usage
    MODIFY COLUMN event_id VARCHAR(32) NOT NULL;

CREATE UNIQUE INDEX ux_llm_usage_event_id ON llm_usage (event_id);

ALTER TABLE mcp_tool_invocations
    ADD COLUMN event_id VARCHAR(32) NULL AFTER id;

UPDATE mcp_tool_invocations
SET event_id = REPLACE(UUID(), '-', '')
WHERE event_id IS NULL OR event_id = '';

ALTER TABLE mcp_tool_invocations
    MODIFY COLUMN event_id VARCHAR(32) NOT NULL;

CREATE UNIQUE INDEX ux_mcp_tool_invocations_event_id ON mcp_tool_invocations (event_id);

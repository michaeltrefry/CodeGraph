ALTER TABLE llm_usage
    MODIFY COLUMN event_id VARCHAR(32) NOT NULL DEFAULT (REPLACE(UUID(), '-', ''));

ALTER TABLE mcp_tool_invocations
    MODIFY COLUMN event_id VARCHAR(32) NOT NULL DEFAULT (REPLACE(UUID(), '-', ''));

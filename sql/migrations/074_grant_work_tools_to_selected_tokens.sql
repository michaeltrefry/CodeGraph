-- One-time grant of the work-tracking tools (sc-27865) to existing selected-mode personal access
-- tokens, following 066: tokens in entitlement_mode = 'all' already see every native tool that is
-- not explicit-only; selected tokens hold an allow-list chosen before these tools existed. Runs
-- once per database (migration_history), so a later deliberate deselection survives restarts.
-- The NOT EXISTS keeps the insert restart-safe.
INSERT INTO mcp_personal_access_token_tool_entitlements (token_id, tool_name, created_at)
SELECT token.id, granted.tool_name, UTC_TIMESTAMP(3)
FROM mcp_personal_access_tokens AS token
CROSS JOIN (
    SELECT 'create_work' AS tool_name
    UNION ALL SELECT 'get_work'
    UNION ALL SELECT 'find_work'
    UNION ALL SELECT 'update_work'
    UNION ALL SELECT 'next_work'
    UNION ALL SELECT 'changes'
) AS granted
WHERE token.entitlement_mode = 'selected'
  AND token.revoked_at IS NULL
  AND NOT EXISTS (
      SELECT 1
      FROM mcp_personal_access_token_tool_entitlements AS existing
      WHERE existing.token_id = token.id
        AND existing.tool_name = granted.tool_name
  );

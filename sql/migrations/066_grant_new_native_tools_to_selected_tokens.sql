-- One-time grant of the sc-25698 native tools to existing selected-mode personal access tokens.
--
-- Tokens in entitlement_mode = 'all' already receive every native tool that is not explicit-only,
-- so they need nothing. Tokens in entitlement_mode = 'selected' hold an allow-list in
-- mcp_personal_access_token_tool_entitlements that was chosen before these tools existed, so the
-- owner decision ("existing tokens get the new tools automatically") is applied here by adding
-- the four tools to every existing, unrevoked selected token.
--
-- This runs exactly once per database (migration_history), so it never re-grants: a user who
-- later deselects one of these tools keeps it deselected across restarts. Tokens created after
-- this migration follow their normal UI selection. The NOT EXISTS keeps the insert restart-safe.
INSERT INTO mcp_personal_access_token_tool_entitlements (token_id, tool_name, created_at)
SELECT token.id, granted.tool_name, UTC_TIMESTAMP(3)
FROM mcp_personal_access_tokens AS token
CROSS JOIN (
    SELECT 'request_overlay' AS tool_name
    UNION ALL SELECT 'get_overlay_status'
    UNION ALL SELECT 'analyze_impact_file'
    UNION ALL SELECT 'file_hotspot'
) AS granted
WHERE token.entitlement_mode = 'selected'
  AND token.revoked_at IS NULL
  AND NOT EXISTS (
      SELECT 1
      FROM mcp_personal_access_token_tool_entitlements AS existing
      WHERE existing.token_id = token.id
        AND existing.tool_name = granted.tool_name
  );

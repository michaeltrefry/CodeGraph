# MCP Hub Operations

## Rollout Gates

1. Apply migration `052_mcp_hub.sql`.
2. Start the API and confirm the MCP Hub catalog seed completes without warnings.
3. Open `/settings/mcp-hub` and confirm the `codegraph`, `shortcut`, `rabbitmq`, and `mysql` providers are listed. External providers should start disabled.
4. Create a user MCP token from `/access-tokens` with a small selected tool set.
5. Call an allowed MCP tool and confirm success.
6. Call a tool not selected for the token and confirm it is absent from `tools/list` and that the call returns HTTP 200 with a JSON-RPC error echoing the request id: `error.code` `-32003`, `error.data.error` `tool_not_entitled`, `error.data.tool` the tool name. Other refusals (`tool_disabled`, `tool_unavailable`, `provider_disabled`, `tool_not_cataloged`, `token_required`) use the same shape.
7. Review `/settings/mcp-hub` audit rows for delegated provider calls.

## Provider Configuration

Shortcut uses credential `shortcut/apiToken`.

RabbitMQ uses config `rabbitmq/managementBaseUrl`, config `rabbitmq/allowedQueues`, and credentials `rabbitmq/username` plus `rabbitmq/password`.

`rabbitmq/allowedQueues` is a comma, semicolon, or newline separated allowlist. Entries use `vhost/queue`, `vhost/*`, or `*/*`. All-vhost listing is not allowed.

MySQL read-only SQL uses credential `mysql/connectionString` by default. Source-specific credentials can use `mysql/connection:{source}`.

`mysql/allowedSources` is required before read-only SQL can run. Use `default` for `mysql/connectionString`, or the `{source}` suffix for `mysql/connection:{source}` credentials.

`mysql/sensitiveColumnPattern` is optional. When omitted, the hub blocks common sensitive column names such as password, token, secret, credential, API key, SSN, and birth-date fields.

## Guardrails

- Provider and tool disabled states are enforced before MCP tool invocation.
- MCP `tools/list` responses are filtered to enabled catalog tools and the calling PAT's exact entitlements, using the same predicate as `tools/call`: a tool is listed exactly when calling it would be dispatched. The SDK's SSE-framed response is filtered in place; a list response the hub cannot parse is refused (JSON-RPC error `tools_list_unfilterable`) rather than passed through unfiltered.
- New native tools reach existing tokens as follows: `all`-mode tokens get every enabled native tool that is not explicit-only automatically. `selected`-mode tokens only get tools on their allow-list, so a one-time data migration grants a new tool to existing selected tokens when that is the product decision (for example `066_grant_new_native_tools_to_selected_tokens.sql` for `request_overlay`, `get_overlay_status`, `analyze_impact_file`, `file_hotspot`). The migration runs once per database, so a tool the owner later deselects is never re-granted.
- PATs created with selected tool access only authorize the exact stored tool names.
- Read-only MySQL SQL accepts a single `SELECT`, `SHOW`, `DESCRIBE`, `DESC`, or `EXPLAIN` statement and appends a bounded `LIMIT` to plain `SELECT` statements.
- RabbitMQ and MySQL provider calls fail closed when their resource/source policies do not explicitly allow the target.
- Provider audit rows include provider, tool, operation, resource, credential mode, authorization decision, status class, duration, token, user, and message context.
- Credential values are encrypted by the MariaDB AES encryptor and are not returned to the frontend after save.

# MCP Hub Operations

## Rollout Gates

1. Apply migration `052_mcp_hub.sql`.
2. Start the API and confirm the MCP Hub catalog seed completes without warnings.
3. Open `/settings/mcp-hub` and confirm the `codegraph`, `shortcut`, `rabbitmq`, and `mysql` providers are listed. External providers should start disabled.
4. Create a user MCP token from `/access-tokens` with a small selected tool set.
5. Call an allowed MCP tool and confirm success.
6. Call a tool not selected for the token and confirm a `403 tool_not_entitled` response.
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
- MCP `tools/list` responses are filtered to enabled catalog tools and the calling PAT's exact entitlements.
- PATs created with selected tool access only authorize the exact stored tool names.
- Read-only MySQL SQL accepts a single `SELECT`, `SHOW`, `DESCRIBE`, `DESC`, or `EXPLAIN` statement and appends a bounded `LIMIT` to plain `SELECT` statements.
- RabbitMQ and MySQL provider calls fail closed when their resource/source policies do not explicitly allow the target.
- Provider audit rows include provider, tool, operation, resource, credential mode, authorization decision, status class, duration, token, user, and message context.
- Credential values are encrypted by the MariaDB AES encryptor and are not returned to the frontend after save.

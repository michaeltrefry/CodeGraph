# MariaDB migrations

These SQL migrations were originally imported from `/Users/michael/Repos/TC.CodeGraphApi/sql/migrations` at donor commit `ccd8d9aa5de63a324177491c585f8f020ca19c78` and now form the runtime schema for the standalone MariaDB provider.

## Runtime ownership and ordering

The API, indexer, memory, metrics, and Jobs hosts all call `MariaDbMigrationRunner` during initialization, before HTTP listeners or background workers start. Migration ownership is coordinated by MariaDB rather than assigned to one container: the first host to acquire the database-scoped `GET_LOCK` advisory lock applies pending scripts, while the other hosts wait and then re-read `migration_history`. This keeps rolling deployments and simultaneous container starts safe without making one application host a permanent infrastructure dependency.

The lock wait defaults to 120 seconds and is configured through `CodeGraph:StorageOptions:MariaDbMigrationLockTimeoutSeconds`. If the lock cannot be acquired, host initialization fails instead of reporting readiness against an unknown schema. MariaDB itself must therefore be reachable before any application host can become ready; RabbitMQ and inter-service readiness do not bypass this database gate.

## Restart safety

MariaDB DDL can implicitly commit, so the runner does not use a transaction as rollback protection. It records every statement in `migration_statement_history` with its script name, ordinal, checksum, status, attempts, and timestamps. A host interruption after a statement commits leaves the row in `started`; the next lock owner replays the same checksummed, restart-safe statement before marking it complete. Completed persistent statements are skipped, the complete dependency chain for session-scoped temporary tables is replayed on each new connection, and a checksum change after execution starts is rejected.

Historical persistent DDL and seed inserts carry `IF [NOT] EXISTS`, existence checks, duplicate-key handling, or migration-specific data postconditions so databases left partially changed by the old runner can advance safely before a statement journal exists. The runner does not globally reinterpret duplicate-object or duplicate-row errors as success: any SQL error is recorded as `failed` and remains fatal. Migration 051 conservatively preserves and quarantines ambiguous pre-cutover embeddings as `legacy-unknown` because the discarded runner's partial ALTER checkpoint cannot distinguish stale default-filled vectors from later valid writes; normal embedding ingestion regenerates current-model vectors.

Do not edit an existing statement after it may have started in any environment. Add a new numbered migration instead.

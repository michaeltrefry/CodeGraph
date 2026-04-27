# MariaDB Migration Inventory

These SQL migrations were imported from `/Users/michael/Repos/TC.CodeGraphApi/sql/migrations` at donor commit `ccd8d9aa5de63a324177491c585f8f020ca19c78` as part of the standalone CodeGraph rebase.

They are source material for the `CodeGraph.Data.MariaDb` provider. The initial provider project includes a migration runner, but these migrations are not wired into runtime startup yet.

Current import range:

- `001_initial_schema.sql` through `043_metric_event_id_defaults.sql`

Follow-up work:

- Verify every migration is idempotent against an empty MariaDB/MySQL database.
- Keep `migration_history` behavior compatible with the donor before enabling startup execution.
- Decide how Neo4j export/import tooling maps into this SQL schema before runtime cutover.

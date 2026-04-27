# Production Deployment

GitHub Actions publishes six images to GHCR on `main` and can deploy them to the public host:

- `ghcr.io/<owner>/codegraph-api:<sha>`
- `ghcr.io/<owner>/codegraph-jobs:<sha>`
- `ghcr.io/<owner>/codegraph-indexer:<sha>`
- `ghcr.io/<owner>/codegraph-memory:<sha>`
- `ghcr.io/<owner>/codegraph-metrics:<sha>`
- `ghcr.io/<owner>/codegraph-web:<sha>`

The deployment job is gated by the repository/environment variable:

```text
CODEGRAPH_DEPLOY_ENABLED=true
```

Required GitHub environment secrets:

```text
CODEGRAPH_DEPLOY_HOST=<ssh host>
CODEGRAPH_DEPLOY_USER=<ssh user>
CODEGRAPH_DEPLOY_SSH_KEY=<private ssh key>
CODEGRAPH_DEPLOY_PATH=/opt/codegraph
CODEGRAPH_PROD_ENV=<complete production .env contents>
```

`CODEGRAPH_PROD_ENV` should include the public auth settings:

```bash
ASPNETCORE_ENVIRONMENT=Production
DOTNET_ENVIRONMENT=Production
CodeGraph__AuthOptions__Enabled=true
CodeGraph__AuthOptions__Authority=https://identity.trefry.net/realms/trefry
CodeGraph__AuthOptions__Audience=codegraph-api
CodeGraph__AuthOptions__ClientId=codegraph-web
CodeGraph__AuthOptions__Scope=openid profile email
CodeGraph__AuthOptions__AuthorizationUrl=https://identity.trefry.net/realms/trefry/protocol/openid-connect/auth
CodeGraph__AuthOptions__TokenUrl=https://identity.trefry.net/realms/trefry/protocol/openid-connect/token
CodeGraph__AuthOptions__EndSessionUrl=https://identity.trefry.net/realms/trefry/protocol/openid-connect/logout
CodeGraph__AuthOptions__AllowedOrigins__0=https://codegraph.trefry.net
CodeGraph__AuthOptions__RequireHttpsMetadata=true
CodeGraph__McpOptions__RequirePersonalAccessToken=true
```

The production compose override in `deploy/docker-compose.production.yml` replaces local builds with GHCR images for API, indexer, memory, metrics, jobs, and web. Keep shared MariaDB/RabbitMQ settings, repository/model mounts, TLS/reverse-proxy paths, provider API keys, and `CodeGraph__InternalServiceAuth__HmacKey` in `CODEGRAPH_PROD_ENV`.

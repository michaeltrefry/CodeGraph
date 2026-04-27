# Production Deployment

GitHub Actions publishes six images to GHCR on `main` and can deploy them to the public host:

- `ghcr.io/<owner>/codegraph-api:<sha>`
- `ghcr.io/<owner>/codegraph-jobs:<sha>`
- `ghcr.io/<owner>/codegraph-indexer:<sha>`
- `ghcr.io/<owner>/codegraph-memory:<sha>`
- `ghcr.io/<owner>/codegraph-metrics:<sha>`
- `ghcr.io/<owner>/codegraph-web:<sha>`

The deployment job is gated by the production environment variable:

```text
CODEGRAPH_DEPLOY_ENABLED=true
```

Required GitHub production environment variables:

```text
CODEGRAPH_DEPLOY_HOST=<ssh host>
CODEGRAPH_DEPLOY_USER=<ssh user>
CODEGRAPH_DEPLOY_PATH=/opt/codegraph
```

Required GitHub production environment secrets:

```text
CODEGRAPH_DEPLOY_SSH_KEY=<private ssh key>
CODEGRAPH__STORAGEOPTIONS__MARIADBCONNECTIONSTRING=<MariaDB connection string>
CODEGRAPH__STORAGEOPTIONS__MARIADBENCRYPTIONKEY=<32-byte encryption key>
CODEGRAPH__RABBITMQOPTIONS__USERNAME=<RabbitMQ username>
CODEGRAPH__RABBITMQOPTIONS__PASSWORD=<RabbitMQ password>
CODEGRAPH__REPOSITORYSOURCE__GITHUB__PERSONALACCESSTOKEN=<GitHub token>
CODEGRAPH__INTERNALSERVICEAUTH__HMACKEY=<internal service HMAC key>
```

The deploy workflow builds the remote `.env` file from individual GitHub production environment variables and secrets. Public/non-sensitive app settings should be stored as environment variables, using upper-case names:

```bash
ASPNETCORE_ENVIRONMENT=Production
DOTNET_ENVIRONMENT=Production
CODEGRAPH__AUTHOPTIONS__ENABLED=true
CODEGRAPH__AUTHOPTIONS__AUTHORITY=https://identity.trefry.net/realms/trefry
CODEGRAPH__AUTHOPTIONS__AUDIENCE=codegraph-api
CODEGRAPH__AUTHOPTIONS__CLIENTID=codegraph-web
CODEGRAPH__AUTHOPTIONS__SCOPE=openid profile email
CODEGRAPH__AUTHOPTIONS__AUTHORIZATIONURL=https://identity.trefry.net/realms/trefry/protocol/openid-connect/auth
CODEGRAPH__AUTHOPTIONS__TOKENURL=https://identity.trefry.net/realms/trefry/protocol/openid-connect/token
CODEGRAPH__AUTHOPTIONS__ENDSESSIONURL=https://identity.trefry.net/realms/trefry/protocol/openid-connect/logout
CODEGRAPH__AUTHOPTIONS__ALLOWEDORIGINS__0=https://codegraph.trefry.net
CODEGRAPH__AUTHOPTIONS__REQUIREHTTPSMETADATA=true
CODEGRAPH__MCPOPTIONS__REQUIREPERSONALACCESSTOKEN=true
```

Optional provider API keys can be added as individual secrets, for example `CODEGRAPH__ANALYSISOPTIONS__OPENAI__APIKEY`, `CODEGRAPH__ANALYSISOPTIONS__GEMINI__APIKEY`, or `CODEGRAPH__ANALYSISOPTIONS__ASSISTANT__ANTHROPIC__APIKEY`.

The production compose override in `deploy/docker-compose.production.yml` replaces local builds with GHCR images for API, indexer, memory, metrics, jobs, and web. Keep shared MariaDB/RabbitMQ settings, repository/model mounts, TLS/reverse-proxy paths, provider API keys, and internal service auth values in GitHub Actions variables/secrets rather than hand-maintaining a bundled `.env` secret.

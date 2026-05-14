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
CODEGRAPH_DEPLOY_SSH_KEY=<private ssh key, or use CODEGRAPH_DEPLOY_SSH_KEY_B64>
CODEGRAPH__STORAGEOPTIONS__MARIADBCONNECTIONSTRING=<MariaDB connection string>
CODEGRAPH__STORAGEOPTIONS__MARIADBENCRYPTIONKEY=<32-byte encryption key>
CODEGRAPH__RABBITMQOPTIONS__USERNAME=<RabbitMQ username>
CODEGRAPH__RABBITMQOPTIONS__PASSWORD=<RabbitMQ password>
CODEGRAPH__REPOSITORYSOURCE__GITHUB__PERSONALACCESSTOKEN=<GitHub token>
CODEGRAPH__INTERNALSERVICEAUTH__HMACKEY=<internal service HMAC key>
```

`CODEGRAPH_DEPLOY_SSH_KEY_B64` can be used instead of `CODEGRAPH_DEPLOY_SSH_KEY` when storing the private key as base64 is more convenient.

Optional GitHub production environment secrets:

```text
CODEGRAPH_DEPLOY_SSH_KEY_PASSPHRASE=<private ssh key passphrase>
```

Embedding model files are downloaded on the remote host during deployment if they are missing. By default the deploy helper writes to `${CODEGRAPH_DOCKER_MODELS_MOUNT:-./.cache/models}/embeddings/nomic-embed-text-v1.5/`, which is the same directory mounted into containers at `/models`.

Optional GitHub production environment variables for the embedding download:

```text
CODEGRAPH_DOCKER_MODELS_MOUNT=/opt/codegraph/models
CODEGRAPH_EMBEDDING_MODEL_ONNX_URL=https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/main/onnx/model.onnx?download=true
CODEGRAPH_EMBEDDING_MODEL_VOCAB_URL=https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/main/vocab.txt?download=true
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

LLM provider tokens, provider endpoints, model lists, default model selections, and analysis/review/assistant token caps are not production deployment secrets. After first boot, configure them from the admin Settings page under **LLM Configuration**; provider tokens are stored encrypted in MariaDB and are never returned by the API. If startup logs include `llm.config.deprecation`, those entries identify appsettings fallback values that should be saved through the admin page.

The deploy workflow intentionally skips `CODEGRAPH__ANALYSISOPTIONS__...` production variables for LLM runtime settings. The only `AnalysisOptions` values that belong in production environment variables are non-LLM CODEGRAPH.md automation settings such as `AutoCommitDocs`, `AutoPushDocs`, and their commit author/message fields.

The production compose override in `deploy/docker-compose.production.yml` replaces local builds with GHCR images for API, indexer, memory, metrics, jobs, and web. Keep shared MariaDB/RabbitMQ settings, repository mounts, TLS/reverse-proxy paths, and internal service auth values in GitHub Actions variables/secrets rather than hand-maintaining a bundled `.env` secret.

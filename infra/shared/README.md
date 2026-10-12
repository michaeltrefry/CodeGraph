# Shared trefry.net infrastructure

These compose files define services on trefry.net that several apps share over the external
Docker network `trefry-network`. They live in the CodeGraph repo so they are in source control.
They are not part of CodeGraph's deployment.

| Service  | Folder      | Used by                  | Container  | Network alias |
|----------|-------------|--------------------------|------------|---------------|
| MariaDB  | `mariadb/`  | Keycloak, CodeGraph      | `mariadb`  | `mariadb`     |
| RabbitMQ | `rabbitmq/` | CodeGraph and other apps | `rabbitmq` | `rabbitmq`    |

## Applied manually, never by CodeGraph's deploy

- CodeGraph's deploy (`.github/workflows/deploy.yml`, `deploy/*.sh`) never references or copies
  `infra/shared`. Merging a CodeGraph change must never restart the shared database or broker.
  `ProductionDeploymentConfigurationTests.DeployNeverTouchesSharedInfrastructure` enforces this.
- Changes here take effect only when someone applies them on the host with the steps below.
  Merging a change to this folder does nothing to the running services.
- Secrets live in an untracked `.env` next to each compose file. `.env.example` lists the variable
  names. `infra/shared/**/.env` is gitignored.

Prerequisite (one-time, already true on trefry.net): `docker network create trefry-network`.

## Apply steps (per service)

Run these on trefry.net as a user who can run Docker. Replace `<service>` with `mariadb` or `rabbitmq`.

```sh
# 1. Copy the folder to the host (from a checkout of this repo)
scp -r infra/shared/<service> root@trefry.net:/opt/trefry/infra/

# 2. On the host: create the .env (first time only; see infra/shared/.env.example for names)
cd /opt/trefry/infra/<service>
touch .env && chmod 600 .env && "${EDITOR:-vi}" .env

# 3. Check the rendered config, then apply
docker compose config
docker compose up -d
docker compose ps
```

`docker compose up -d` recreates the container only if its configuration changed. Recreating
MariaDB or RabbitMQ briefly interrupts every app that uses it, so do it in a quiet window.

## MariaDB: switch from /root/mariadb.docker-compose.yml to the repo copy

The live definition is `/root/mariadb.docker-compose.yml`, with the passwords inline. The repo copy
is the same definition with the passwords moved to `.env`.

1. Copy the folder to `/opt/trefry/infra/mariadb` and create `.env` with the `MARIADB_ROOT_PASSWORD`
   and `MARIADB_PASSWORD` values from the live file (apply steps 1-2 above).
2. Diff the rendered configs. The only differences should be cosmetic, such as the project name
   and ordering:
   ```sh
   docker compose -f /root/mariadb.docker-compose.yml config > /tmp/mariadb-live.yml
   docker compose -f /opt/trefry/infra/mariadb/docker-compose.yml config > /tmp/mariadb-repo.yml
   diff /tmp/mariadb-live.yml /tmp/mariadb-repo.yml
   rm /tmp/mariadb-live.yml /tmp/mariadb-repo.yml   # these contain the passwords
   ```
3. Switch over. `container_name: mariadb` and the data path `/opt/trefry/mariadb/data` are the
   same, so the data carries over. The compose project name comes from the folder name, so the
   new project may not adopt the old container. Stop and remove the old one first. The bind-mounted
   data directory is not deleted.
   ```sh
   docker compose -f /root/mariadb.docker-compose.yml down     # removes the container only; data stays on the bind mount
   cd /opt/trefry/infra/mariadb && docker compose up -d
   docker compose logs --tail=50 mariadb
   docker exec mariadb mariadb -uroot -p -e 'SHOW DATABASES; SELECT @@innodb_buffer_pool_size;'
   ```
4. Once healthy, move the old file aside, for example
   `mv /root/mariadb.docker-compose.yml /root/mariadb.docker-compose.yml.migrated`, so there is one
   source of truth.

Rollback: `cd /opt/trefry/infra/mariadb && docker compose down`, then
`docker compose -f /root/mariadb.docker-compose.yml up -d`. The data directory is shared, so
nothing is lost.

## RabbitMQ: one-time migration from the hand-run container

The live broker was started with plain `docker run`, not compose. The facts that matter:

- **Hostname `439e97697844`.** RabbitMQ keeps its data under `rabbit@<hostname>`, so a new
  hostname starts an empty broker. The compose file pins this hostname.
- **Anonymous volume `974d2f346a35fc36e0285721e2157842b695764cb2aedaa5dc56316fc8a05fad`** mounted at
  `/var/lib/rabbitmq`. The compose file adopts it as an external volume.
- **Restart policy `no`.** The live broker does not come back after a host reboot. The compose
  file changes this to `unless-stopped`, so after the migration the broker survives reboots.
- **Image.** The live container uses `rabbitmq:3-management`, which is 3.13.7. The compose file
  pins `rabbitmq:3.13.7-management`.
- **Ports.** 5672 and 15672 are published on `0.0.0.0` and stay that way. See the warning in
  `rabbitmq/docker-compose.yml`. Bind them to `127.0.0.1` once no remote client needs them.

Steps:

```sh
# 1. Verify the facts still match the compose file
docker inspect rabbitmq --format '{{.Config.Hostname}}'                     # expect 439e97697844
docker inspect rabbitmq --format '{{range .Mounts}}{{.Name}} -> {{.Destination}}{{"\n"}}{{end}}'
                                                                             # expect 974d2f34...fad -> /var/lib/rabbitmq
docker exec rabbitmq rabbitmqctl version                                     # expect 3.13.7
docker exec rabbitmq rabbitmqctl list_users
docker exec rabbitmq rabbitmqctl list_queues name messages                   # note these for comparison
# If the hostname or volume differ, update rabbitmq/docker-compose.yml before continuing.

# 2. Copy the folder to /opt/trefry/infra/rabbitmq and create .env with RABBITMQ_DEFAULT_PASS
#    (apply steps 1-2 above), then check the config
cd /opt/trefry/infra/rabbitmq && docker compose config

# 3. Stop the old container and keep it, renamed, for rollback (this does NOT touch the volume)
docker stop rabbitmq && docker rename rabbitmq rabbitmq-old

# 4. Start the compose-managed broker on the same volume and hostname
docker compose up -d
docker compose logs --tail=50 rabbitmq

# 5. Confirm the existing data is present (same users and queues as step 1)
docker exec rabbitmq rabbitmqctl list_users
docker exec rabbitmq rabbitmqctl list_queues name messages
docker inspect rabbitmq --format '{{.HostConfig.RestartPolicy.Name}}'        # expect unless-stopped

# 6. After the apps reconnect, remove the old container. Do NOT pass -v: the volume must be kept.
docker rm rabbitmq-old
```

Rollback (before step 6):

```sh
cd /opt/trefry/infra/rabbitmq && docker compose down   # the volume is external, so compose does not remove it
docker rename rabbitmq-old rabbitmq
docker start rabbitmq
```

Never run `docker volume rm` or `docker compose down -v` against
`974d2f346a35fc36e0285721e2157842b695764cb2aedaa5dc56316fc8a05fad`. It holds all broker data.

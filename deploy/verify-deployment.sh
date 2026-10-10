#!/bin/sh
# Post-deploy verification. Run from the deploy directory after `docker compose up -d`.
# Waits (bounded) until every compose service has a container that runs the deployed IMAGE_TAG,
# is running, is not restarting, and reports a healthy docker healthcheck; then confirms no
# container restarted during a short stability window. Every host applies MariaDB migrations
# before it listens, so "healthy" also means migrations completed. On failure it prints
# `docker compose ps` plus recent logs of each unready service and exits non-zero.
set -eu

: "${IMAGE_TAG:?IMAGE_TAG is required}"
: "${IMAGE_PREFIX:?IMAGE_PREFIX is required}"
export IMAGE_TAG IMAGE_PREFIX

timeout_seconds="${CODEGRAPH_DEPLOY_VERIFY_TIMEOUT_SECONDS:-600}"
poll_seconds="${CODEGRAPH_DEPLOY_VERIFY_POLL_SECONDS:-10}"
stable_seconds="${CODEGRAPH_DEPLOY_VERIFY_STABLE_SECONDS:-30}"
max_restarts="${CODEGRAPH_DEPLOY_VERIFY_MAX_RESTARTS:-3}"
log_lines="${CODEGRAPH_DEPLOY_VERIFY_LOG_LINES:-100}"

compose() {
  docker compose -f docker-compose.yml -f docker-compose.production.yml "$@"
}

services="$(compose config --services)"
if [ -z "$services" ]; then
  echo "::error::docker compose reported no services" >&2
  exit 1
fi

# Prints "<image>|<status>|<restarting>|<restart count>|<health>" for a service, or nothing when
# the service has no container.
service_state() {
  container_id="$(compose ps -a -q "$1" 2>/dev/null | head -n 1)"
  if [ -z "$container_id" ]; then
    return
  fi

  docker inspect -f '{{.Config.Image}}|{{.State.Status}}|{{.State.Restarting}}|{{.RestartCount}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$container_id"
}

# Sets `reason` and returns non-zero when the service is not ready.
check_service() {
  state="$(service_state "$1")"
  if [ -z "$state" ]; then
    reason="no container"
    return 1
  fi

  image="$(echo "$state" | cut -d'|' -f1)"
  status="$(echo "$state" | cut -d'|' -f2)"
  restarting="$(echo "$state" | cut -d'|' -f3)"
  restarts="$(echo "$state" | cut -d'|' -f4)"
  health="$(echo "$state" | cut -d'|' -f5)"
  reason="image=$image status=$status restarting=$restarting restarts=$restarts health=$health"

  case "$image" in
    *":$IMAGE_TAG") ;;
    *) return 1 ;;
  esac
  [ "$status" = "running" ] && [ "$restarting" = "false" ] && [ "$health" = "healthy" ]
}

restart_count() {
  service_state "$1" | cut -d'|' -f4
}

fail() {
  echo "::error::$1"
  echo "----- docker compose ps -----"
  compose ps -a || true
  for service in $services; do
    if ! check_service "$service" || [ "$(restart_count "$service")" != "0" ]; then
      echo "----- $service: $reason -----"
      container_id="$(compose ps -a -q "$service" 2>/dev/null | head -n 1)"
      if [ -n "$container_id" ]; then
        docker inspect -f '{{if .State.Health}}{{range .State.Health.Log}}healthcheck exit={{.ExitCode}}: {{.Output}}{{println}}{{end}}{{end}}' "$container_id" | tail -n 5 || true
      fi
      echo "----- last $log_lines log lines of $service -----"
      compose logs --no-color --tail="$log_lines" "$service" || true
    fi
  done
  exit 1
}

echo "Waiting up to ${timeout_seconds}s for services to run ${IMAGE_TAG} and report healthy: $(echo "$services" | tr '\n' ' ')"
start="$(date +%s)"
while :; do
  pending=""
  for service in $services; do
    if ! check_service "$service"; then
      pending="$pending $service"
      echo "  waiting: $service ($reason)"
      if [ -n "${restarts:-}" ] && [ "${restarts:-0}" -ge "$max_restarts" ]; then
        fail "$service restarted $restarts times (crash loop); see logs below"
      fi
    fi
    restarts=""
  done

  if [ -z "$pending" ]; then
    break
  fi

  elapsed=$(( $(date +%s) - start ))
  if [ "$elapsed" -ge "$timeout_seconds" ]; then
    fail "Services not healthy on ${IMAGE_TAG} after ${timeout_seconds}s:${pending}"
  fi
  sleep "$poll_seconds"
done

baseline=""
for service in $services; do
  baseline="$baseline $service=$(restart_count "$service")"
done

echo "All services healthy; confirming no restarts for ${stable_seconds}s"
sleep "$stable_seconds"

for service in $services; do
  if ! check_service "$service"; then
    fail "$service became unready during the stability window ($reason)"
  fi
  case " $baseline " in
    *" $service=$(restart_count "$service") "*) ;;
    *) fail "$service restarted during the stability window" ;;
  esac
done

echo "Deployment verified: all services run ${IMAGE_TAG} and are healthy"
compose ps

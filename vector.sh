#!/bin/sh
set -e
# Add Vector-sink configs for all peer machines in the app.
api() { curl -sS -H "Authorization: ${ACCESS_TOKEN?}" http://_api.internal:4280/v1/$1; }
dir=/etc/vector/sinks; mkdir -p $dir
for peer in $(api "apps/$FLY_APP_NAME/machines" | jq -r .[].id | grep -v "$FLY_MACHINE_ID"); do
  cat <<YAML > "$dir/$peer.yaml"
type: vector
inputs: ['logs', '*-metrics']
healthcheck: {enabled: false}
compression: true
address: "$peer.vm.$FLY_APP_NAME.internal:6000"
buffer: {type: disk, max_size: ${DISK_BUFFER:-268435488}, when_full: drop_newest}
YAML
  done
export VECTOR_WATCH_CONFIG=true
export VECTOR_CONFIG_DIR=/etc/vector
# Optional sinks (fly-log-shipper pattern): definitions live in
# vector-sinks/ in the repo, staged at /etc/vector-optional/, and copied
# into the live CONFIG_DIR only when their activating secret is set — see
# vector-sinks/logtail.yaml for why they can't sit in /etc/vector.
[ -n "${LOGTAIL_TOKEN}" ] && cp /etc/vector-optional/logtail.yaml /etc/vector/logtail.yaml
# Vector exits if the NATS platform streams reject a freshly-minted token that
# hasn't propagated to Fly's auth backend yet (or on any transient boot failure).
# Grafana is the machine's foreground process, so a dead Vector would otherwise
# go unnoticed while ingestion stays silently broken — keep restarting it.
while :; do
  vector || echo "vector exited (code $?); restarting in 5s" >&2
  sleep 5
done

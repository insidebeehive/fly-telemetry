#!/bin/sh
set -e

# Fly platform telemetry bridge.
#
# The NATS platform streams are the one source ClickStack's OTel collector
# cannot read, and platform metrics (fly_*: machine CPU/memory, proxy and edge
# counters) can never come from an SDK — nothing in your process emits them.
# Each direction is an independent env toggle so logs can move to the SDKs
# later by flipping a variable and restarting, with no rebuild:
#
#   INGEST_NATS_LOGS=true|false      (default true — no SDKs deployed yet)
#   INGEST_NATS_METRICS=true|false   (default true — the only way to get fly_*)

dir=/etc/vector
mkdir -p "$dir"
rm -f "$dir"/*.yaml

if [ "${INGEST_NATS_METRICS:-true}" = "true" ]; then
  cp /etc/bhclickstack/vector-templates/nats-metrics.yaml "$dir/"
  echo "vector: NATS metrics bridge ENABLED"
else
  echo "vector: NATS metrics bridge disabled"
fi

if [ "${INGEST_NATS_LOGS:-true}" = "true" ]; then
  cp /etc/bhclickstack/vector-templates/nats-logs.yaml "$dir/"
  echo "vector: NATS logs bridge ENABLED"
else
  echo "vector: NATS logs bridge disabled (expecting logs from the SDKs)"
fi

if [ -z "$(ls -A "$dir" 2>/dev/null)" ]; then
  echo "vector: both NATS bridges are off — not starting vector" >&2
  exit 0
fi

: "${ACCESS_TOKEN:?ACCESS_TOKEN is required while a NATS bridge is enabled}"

export VECTOR_CONFIG_DIR="$dir"

# Vector exits permanently when a source fails at topology build — e.g. a
# freshly minted token that has not propagated to Fly's auth backend yet.
# Nothing else in this container watches it, so keep restarting it. Same lesson
# as the bhgrafana stack (../DEVLOG.md 2026-08-28).
while :; do
  vector || echo "vector exited (code $?); restarting in 5s" >&2
  sleep 5
done

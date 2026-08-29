#!/bin/sh
set -e

# The Fly volume mounts at /data and SHADOWS the image's own /data/db, so on a
# fresh volume mongod dies instantly with "NonExistentPath: Data directory
# /data/db not found" — and because HyperDX keeps all its state in Mongo, the
# API then 500s on every OpAMP request, the collector never receives its
# receivers, and OTLP ingest never starts. ClickHouse's entrypoint creates and
# chowns its own path; mongod does not. Create both before handing off.
mkdir -p /data/db /data/clickhouse

# Start the Fly NATS bridge alongside the stock ClickStack processes, then hand
# off to the upstream entrypoint unchanged. That script starts ClickHouse,
# MongoDB, the OTel collector and the HyperDX app, and ends with `wait -n` — so
# if any of them dies the container exits and Fly restarts the machine (fail
# fast rather than the half-alive state bhgrafana hit in 2026-08). Vector is not
# in that wait set, so vector.sh supervises itself.
/etc/bhclickstack/vector.sh &

exec sh /etc/local/entry.sh

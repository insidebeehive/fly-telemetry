#!/bin/sh
set -e

# Start the Fly NATS bridge alongside the stock ClickStack processes, then hand
# off to the upstream entrypoint unchanged. That script starts ClickHouse,
# MongoDB, the OTel collector and the HyperDX app, and ends with `wait -n` — so
# if any of them dies the container exits and Fly restarts the machine (fail
# fast rather than the half-alive state bhgrafana hit in 2026-08). Vector is not
# in that wait set, so vector.sh supervises itself.
/etc/bhcs/vector.sh &

exec sh /etc/local/entry.sh

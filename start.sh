#!/bin/sh
set -e

: "${ACCESS_TOKEN?}"
export enableTCP6=true
/victoria-metrics-prod -envflag.enable -storageDataPath /data/metrics &
/victoria-logs-prod -envflag.enable -storageDataPath /data/logs &
/victoria-traces-prod -envflag.enable -storageDataPath /data/traces -retentionPeriod 14d -httpListenAddr :10428 &
/vector.sh &

/run.sh

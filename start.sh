#!/bin/sh
set -e

: "${ACCESS_TOKEN?}"
export enableTCP6=true
# Retention policy (2026-08-29): logs 60d (fleet-wide incl. HTTP bodies),
# metrics 14d, traces 14d at 10% sampling. Disk caps keep any one store from
# starving the others if ingest outgrows the volume — oldest partitions drop first.
/victoria-metrics-prod -envflag.enable -storageDataPath /data/metrics -retentionPeriod 14d &
/victoria-logs-prod -envflag.enable -storageDataPath /data/logs -retentionPeriod 60d -retention.maxDiskSpaceUsageBytes 35GiB &
/victoria-traces-prod -envflag.enable -storageDataPath /data/traces -retentionPeriod 14d -retention.maxDiskSpaceUsageBytes 8GiB -httpListenAddr :10428 &
/vector.sh &

/run.sh

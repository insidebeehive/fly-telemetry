#!/bin/sh
set -e

: "${ACCESS_TOKEN?}"
export enableTCP6=true
# File descriptors (2026-09-12). Fly's init hands the machine a 10240 open-file
# limit (soft=hard). VictoriaLogs keeps every file of every part it has touched
# open — one 24h dashboard query opened ~2,200 files on top of its ~850 baseline
# (measured live) — and when a merge or flush then cannot create a file it
# panics: "FATAL: cannot create file … too many open files" (rc=2). Three such
# crashes 09-10..09-12, one per day, each caught by the supervisor in 3s with
# zero loss thanks to the Vector disk buffer; the probable cause of the 09-07
# silent death too. Root may raise the hard limit up to fs.nr_open (1048576),
# verified on the machine; the stores inherit it from this shell.
ulimit -n 1048576 || echo "[start] could not raise the open-file limit" >&2
# Retention policy (2026-08-29): logs 60d (fleet-wide incl. HTTP bodies),
# metrics 14d, traces 14d at 10% sampling. Disk caps keep any one store from
# starving the others if ingest outgrows the volume — oldest partitions drop first.

# Supervision (2026-09-07). Grafana (/run.sh) is the machine's foreground
# process, so a VictoriaX binary that dies in the background leaves the machine
# "started" while its store is silently gone. VictoriaLogs did exactly that on
# 2026-09-07 11:36Z: ~55 min outage, ~4M lines lost, and the cause was
# unrecoverable because its last stderr line had nowhere durable to land.
# Mirror vector.sh: restart on exit, and log the exit code + signal
# (137=SIGKILL/OOM, 134=SIGABRT/Go fatal, 139=SIGSEGV) so the NEXT death leaves
# a trail in the bhgrafana stream once the store is back seconds later.
supervise() {
  name="$1"; shift
  (
    set +e
    while :; do
      "$@"; rc=$?
      sig=""; [ "$rc" -gt 128 ] && sig=" signal=$((rc-128))"
      echo "[supervisor] $name exited rc=$rc$sig — restarting in 3s" >&2
      sleep 3
    done
  ) &
}
supervise victoria-metrics /victoria-metrics-prod -envflag.enable -storageDataPath /data/metrics -retentionPeriod 180d
supervise victoria-logs    /victoria-logs-prod -envflag.enable -storageDataPath /data/logs -retentionPeriod 60d -retention.maxDiskSpaceUsageBytes 120GiB
supervise victoria-traces  /victoria-traces-prod -envflag.enable -storageDataPath /data/traces -retentionPeriod 14d -retention.maxDiskSpaceUsageBytes 30GiB -httpListenAddr :10428
/vector.sh &

/run.sh

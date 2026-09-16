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
# Retention policy (2026-09-17, supersedes 2026-08-29 and the undocumented
# 09-03 metrics bump to 180d): logs 60d, metrics 30d, traces 30d at 10%
# sampling. Owner rule: metrics + traces get ~20 GiB between them, logs get
# everything left over.
#
# Disk budget, measured on the live box 09-17 (df: 196.73 GiB nominal, of which
# 8.1 GiB is ext4 root-reserved => 188.6 GiB usable for planning):
#   logs cap       150 GiB   (60d needs ~166 GiB, so the cap still binds first)
#   traces cap      15 GiB   (30d x 0.37 GiB/day ~ 11 GiB, 1.4x headroom)
#   metrics        ~5 GiB    (30d x 0.16 GiB/day; NO hard cap — see below)
#   vector buffer    2 GiB   (logs_db disk buffer, worst case; 56 MiB in practice)
#   grafana + misc   1 GiB
#   free headroom ~12.6 GiB  (merge/spike slack, plus the 8.1 GiB ext4 reserve)
# Measured rates: logs 2.76 GiB/day (range 2.45–3.00), traces 0.37, metrics 0.16.
#
# So logs now hold ~54 days (50–61 depending on traffic) instead of the 43 the
# old 120 GiB cap allowed. The 60d flag is still the intent, not the bound — a
# true 60d needs ~166 GiB, which does not fit beside the other stores on a
# 200 GB volume. Closing the last ~6 days needs a 300 GB volume or less log
# ingest (bo-api-casino is 39% of log bytes, res_body alone 27%). See DEVLOG.md.
#
# Metrics is deliberately uncapped: VictoriaMetrics requires
# -retention.maxDiskSpaceUsageBytes to exceed ~2x its biggest monthly partition,
# and a value below that makes it refuse to start — which the supervise() loop
# would turn into a crash loop. 30d retention is its bound instead.

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
supervise victoria-metrics /victoria-metrics-prod -envflag.enable -storageDataPath /data/metrics -retentionPeriod 30d
supervise victoria-logs    /victoria-logs-prod -envflag.enable -storageDataPath /data/logs -retentionPeriod 60d -retention.maxDiskSpaceUsageBytes 150GiB
supervise victoria-traces  /victoria-traces-prod -envflag.enable -storageDataPath /data/traces -retentionPeriod 30d -retention.maxDiskSpaceUsageBytes 15GiB -httpListenAddr :10428
/vector.sh &

/run.sh

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
#   logs cap       160 GiB   (raised from 150 on 09-18 — see below)
#   traces cap      15 GiB   (30d x 0.37 GiB/day ~ 11 GiB, 1.4x headroom)
#   metrics        ~5 GiB    (30d x 0.16 GiB/day; NO hard cap — see below)
#   vector buffer    2 GiB   (logs_db disk buffer, worst case; 56 MiB in practice)
#   grafana + misc   1 GiB
#   free headroom  ~10 GiB   (merge/spike slack, plus the 8.1 GiB ext4 reserve)
#
# 60d IS NOW REACHABLE (2026-09-18). Suppressing res_body on five bo-api-casino
# read routes cut that app's response bytes 88.7% (3.409 -> 0.385 GB/day) with
# traffic flat, and fleet log growth from 2.89 to 2.47 GiB/day measured across
# the deploy boundary on 25 hourly samples. 60d x 2.47 = 148 GiB, which fits
# 150 — but by only 1.2%, and the week before the change ranged 2.47–2.87
# GiB/day. 160 GiB holds 60 days up to 2.67 GiB/day instead of 2.50, which
# covers most of that range and leaves room for new package adopters. A cap is
# a ceiling, not an allocation: nothing is consumed until logs reach it.
#
# Note the disk saving was ~half the naive estimate (0.42 GiB/day, not 0.72):
# converting raw bytes at the fleet-average 4.2:1 overstated it, because 5.3M
# near-identical getbalance responses were the most compressible data in the
# store (~7:1). Suppressing the most repetitive data frees proportionally less
# disk than its raw share suggests — remember this before the next estimate.
# See DEVLOG.md.
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
supervise victoria-logs    /victoria-logs-prod -envflag.enable -storageDataPath /data/logs -retentionPeriod 60d -retention.maxDiskSpaceUsageBytes 160GiB
supervise victoria-traces  /victoria-traces-prod -envflag.enable -storageDataPath /data/traces -retentionPeriod 30d -retention.maxDiskSpaceUsageBytes 15GiB -httpListenAddr :10428
/vector.sh &

/run.sh

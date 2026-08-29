# ClickStack all-in-one (ClickHouse + HyperDX + OTel collector + MongoDB),
# packaging-only overlay — no upstream source is modified, this is NOT a fork.
# Images moved from docker.hyperdx.io/hyperdx/* to clickhouse/clickstack-*;
# pin exact versions and read the ClickStack changelog before every bump.
FROM clickhouse/clickstack-all-in-one:2.37.0

# ClickHouse overrides: data + spill on the Fly volume, memory governance so
# the kernel OOM killer can never take out ingestion (see ../DEVLOG.md).
COPY clickhouse/config.d/ /etc/clickhouse-server/config.d/
COPY clickhouse/users.d/  /etc/clickhouse-server/users.d/

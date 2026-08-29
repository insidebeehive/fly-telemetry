ARG CLICKSTACK_TAG=2.37.0
# Pinned: vector >= 0.57 fails NATS auth against Fly's platform streams (../DEVLOG.md 2026-08-28)
ARG VECTOR_TAG=0.46.1-distroless-static

FROM timberio/vector:${VECTOR_TAG} AS vector

# Packaging-only overlay. Upstream ClickStack is UNMODIFIED — this is not a fork.
# Images moved from docker.hyperdx.io/hyperdx/* to Docker Hub clickhouse/clickstack-*.
FROM clickhouse/clickstack-all-in-one:${CLICKSTACK_TAG}

# Vector bridges the Fly NATS platform streams, which the OTel collector cannot read.
COPY --from=vector /usr/local/bin/vector /usr/local/bin/

# ClickHouse: data on the volume + memory governance (see the files themselves).
COPY clickhouse/config.d/ /etc/clickhouse-server/config.d/
COPY clickhouse/users.d/  /etc/clickhouse-server/users.d/

# Extra collector pipeline for the NATS metrics bridge. The upstream entrypoint
# appends this file to the collector's config list when CUSTOM_OTELCOL_CONFIG_FILE
# is set (verified in the image's /otel-entrypoint.sh, both supervisor and
# standalone modes).
COPY otel/custom.config.yaml /etc/bhcs/otel-custom.config.yaml

COPY vector-templates/ /etc/bhcs/vector-templates/
COPY vector.sh entry.sh /etc/bhcs/
RUN chmod +x /etc/bhcs/vector.sh /etc/bhcs/entry.sh

# Upstream ENTRYPOINT is ["sh", "/etc/local/entry.sh"]; ours starts the NATS
# bridge and then hands off to it unchanged.
ENTRYPOINT ["sh", "/etc/bhcs/entry.sh"]

LABEL org.opencontainers.image.source="https://github.com/insidebeehive/fly-telemetry"

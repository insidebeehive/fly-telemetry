ARG VICTORIA_METRICS_TAG=v1.118.0
# v1.50.0 (was v1.22.2): fixes the sort-OOM on `| sort by (_time) desc` + HTTP
# limit=N — the Grafana log-explorer query. The limit-pushdown fix landed in
# v1.24.0 (VictoriaLogs #129); #802 (~v1.39) fixed a related off-by-one. v1.50.0
# is the last Alpine base before v1.52.0 went distroless (keeps /victoria-logs-prod
# path), and sits before v1.51.0's cluster-protocol/filter-syntax changes (N/A
# single-node either way). Storage is forward-compatible; downgrade unsupported
# (volume snapshots are the rollback). See DEVLOG.md.
ARG VICTORIA_LOGS_TAG=v1.50.0
# VictoriaTraces is pre-GA: pin exact versions and read the changelog before bumping (see DEVLOG.md)
ARG VICTORIA_TRACES_TAG=v0.10.0
# Pinned: vector >= 0.57 fails NATS auth against Fly's platform streams (see DEVLOG.md)
ARG VECTOR_TAG=0.46.1-distroless-static

FROM victoriametrics/victoria-metrics:${VICTORIA_METRICS_TAG} AS metrics
FROM victoriametrics/victoria-logs:${VICTORIA_LOGS_TAG} AS logs
FROM victoriametrics/victoria-traces:${VICTORIA_TRACES_TAG} AS traces
FROM timberio/vector:${VECTOR_TAG} AS vector
FROM grafana/grafana-oss:main
COPY --link --from=metrics /victoria-metrics-prod /
COPY --link --from=logs /victoria-logs-prod /
COPY --link --from=traces /victoria-traces-prod /
COPY --link --from=vector /usr/local/bin/vector /usr/local/bin/
RUN grafana cli plugins install victoriametrics-logs-datasource && \
    grafana cli plugins install victoriametrics-metrics-datasource
COPY vector.yaml /etc/vector/
# Optional sink definitions, staged OUTSIDE /etc/vector: CONFIG_DIR loads
# every file in there, so vector.sh copies a sink in only when its
# activating secret is set. Add future sinks to vector-sinks/ — no
# Dockerfile change needed.
COPY vector-sinks/ /etc/vector-optional/
COPY start.sh /
COPY vector.sh /
COPY dashboards/grafana/ /var/lib/grafana-dashboards/
# Repo-owned dashboards — appear as their own folder alongside the bundled
# Fly ones via the provider's foldersFromFilesStructure.
COPY grafana-dashboards/ /var/lib/grafana-dashboards/
COPY datasources.yml /etc/grafana/provisioning/datasources/
COPY dashboards.yml /etc/grafana/provisioning/dashboards/
COPY grafana.ini /etc/grafana/

USER root
RUN apk add --no-cache jq
WORKDIR /
ENTRYPOINT []
ENV GF_PATHS_DATA=/data/grafana
LABEL maintainer="fly.io"
LABEL org.opencontainers.image.source="https://github.com/insidebeehive/fly-telemetry"
CMD ["/start.sh"]

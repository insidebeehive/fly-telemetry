ARG VICTORIA_METRICS_TAG=v1.118.0
ARG VICTORIA_LOGS_TAG=v1.22.2-victorialogs
# VictoriaTraces is pre-GA: pin exact versions and read the changelog before bumping (see DEVLOG.md)
ARG VICTORIA_TRACES_TAG=v0.10.0

FROM victoriametrics/victoria-metrics:${VICTORIA_METRICS_TAG} AS metrics
FROM victoriametrics/victoria-logs:${VICTORIA_LOGS_TAG} AS logs
FROM victoriametrics/victoria-traces:${VICTORIA_TRACES_TAG} AS traces
FROM timberio/vector:latest-distroless-static AS vector
FROM grafana/grafana-oss:main
COPY --link --from=metrics /victoria-metrics-prod /
COPY --link --from=logs /victoria-logs-prod /
COPY --link --from=traces /victoria-traces-prod /
COPY --link --from=vector /usr/local/bin/vector /usr/local/bin/
RUN grafana cli plugins install victoriametrics-logs-datasource && \
    grafana cli plugins install victoriametrics-metrics-datasource
COPY vector.yaml /etc/vector/
COPY start.sh /
COPY vector.sh /
COPY dashboards/grafana/ /var/lib/grafana-dashboards/
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

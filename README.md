# Fly Telemetry

This is a simple, lightweight reference implementation for quick, out-of-the-box observability
for simple deployments on Fly.io.

This fork adds distributed tracing via [VictoriaTraces](https://docs.victoriametrics.com/victoriatraces/)
(OTLP ingest on port 10428, queried in Grafana through the Tempo datasource). Because of that,
the app is built from the local `Dockerfile` instead of the upstream prebuilt image — see DEVLOG.md
for decisions and version pins.

## Getting Started

Clone this repo **with submodules** (the bundled dashboards are a submodule and the Docker build needs them),
run `fly launch` to create a new app, create a readonly access token, and deploy:
```shell
git clone --recurse-submodules https://github.com/insidebeehive/fly-telemetry.git && cd fly-telemetry
ORG=my-org-name
fly launch --copy-config -y --org $ORG -e ORG=$ORG --no-deploy
fly secrets set ACCESS_TOKEN="$(fly tokens create readonly $ORG)" --stage
fly deploy --flycast 
```
Keep the app private: deploy with `--flycast` only and verify no public IPs with `fly ips list`
after every launch/config change — the private network is the only auth boundary.

Once the deploy finishes, you can access the Grafana service to view your collected logs+metrics over your private network at `http://$FLY_APP_NAME.flycast/`.

## Features

- Subscribes to logs+metrics from the Fly.io-provided NATS platform streams on `[fdaa::3]:4223`.
- Writes logs to local VictoriaLogs and metrics to local VictoriaMetrics for storage.
- Accepts OTLP traces over HTTP into local VictoriaTraces (14-day retention).
- Runs a local Grafana instance with preconfigured data sources and dashboards for visualization and alerting.

## Traces

Apps send traces directly to this app over the private network — no OTel collector in between.
Configure each instrumented service with:

```
OTEL_SERVICE_NAME=$FLY_APP_NAME
OTEL_EXPORTER_OTLP_ENDPOINT=http://<this-app>.flycast:10428/insert/opentelemetry
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_RESOURCE_ATTRIBUTES=fly.region=$FLY_REGION,deployment.environment=prod
```

⚠️ The endpoint is the **base path** — the SDK appends `/v1/traces` itself. A wrong base
means silent 404s and a "no traces" mystery.

Traces are queried in Grafana via the preprovisioned **VictoriaTraces** datasource
(Tempo-compatible API at `/select/tempo`). Smoke-test ingest from any machine on the network:

```shell
curl http://<this-app>.flycast:10428/select/tempo/api/echo
```

The app runs in a single monolithic instance, which you can vertically scale for small-to-medium sized orgs. Once you grow out of this setup, you can fork and modify
this template to further extend it yourself for clustered storage, or [ship data](https://github.com/superfly/fly-log-shipper) directly to managed services that can offer greater scale and support.

## Security

The app doesn't configure any authentication, it's a simple template for internal use on a private Flycast network.
**Do not deploy this app with a public IP without setting up your own authentication!**

Grafana is configured for anonymous admin access, which you can reach on its default port 3000 over 6pn,
or on port 80 through Flycast. Either [connect to a Wireguard peer](https://fly.io/docs/blueprints/connect-private-network-wireguard/) to your org's private network
and access `$FLY_APP_NAME.flycast`. `$FLY_APP_NAME.internal:3000`, or run `fly proxy 3000` and access `localhost:3000`.

## High Availability

This app supports a simple High Availability mode by scaling out to multiple Machines, in the same or different region.

Each instance subscribes to the platform streams on a shared [queue group](https://docs.nats.io/nats-concepts/core-nats/queue) which
load-balances data across each active instance, and data is replicated to all other peers through Vector's native gRPC stream.
Flycast load-balances service requests to the nearest available instance. For HA alerting, Grafana is configured to use the High Availability Alertmanager
implementation that suppresses duplicate alert notifications across the cluster.

Each Grafana instance uses its own local SQLite database by default. You must change this config to use
a shared external database cluster (like Postgres), or for a more minimal approach, use only file-provisioned
dashboards, alert rules, etc., so all state is consistently maintained across both Grafana instances.

The replication topology is resolved at boot, so you will need to restart any running instances after making changes to
the cluster configuration (like scaling up/down or replacing app instances).

## Configuration

Required env variables (provide with `fly secrets set`, or modify `fly.toml`):

* `ACCESS_TOKEN` (`fly secrets set ACCESS_TOKEN=$(fly tokens create readonly)`)
* `ORG` (default `personal`)

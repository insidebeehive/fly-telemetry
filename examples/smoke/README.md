# Telemetry smoke-test app

Tiny express app exercising every feature of `@insidebeehive/telemetry`:
`/health` (must be ignored), `/hello`, `POST /bets` (bodies + redaction +
`logger.audit`), `/error` (payload policy), `/slow` (slow tier), `/crash` /
`/reject` (crash handlers). Two identical entries: `server.js` (CJS,
`--require` path) and `server.mjs` (ESM, `--import` loader-hook path).

## Run locally

```sh
cd examples/smoke && npm install   # installs the published package from npm
# (testing unreleased package changes: npm pack ../../telemetry && npm i ./insidebeehive-telemetry-*.tgz)
# inert (off-Fly) behaviour:
node --require @insidebeehive/telemetry/register server.js
# full pipeline with console span exporter:
PORT=3777 FLY_APP_NAME=smoke-local NODE_ENV=production \
  OTEL_TRACES_EXPORTER=console OTEL_TRACES_SAMPLER_ARG=1 HTTP_LOG_PAYLOAD=always \
  node --import @insidebeehive/telemetry/register server.mjs
# Bun (tracing skips, http+app logging active):
bun --require @insidebeehive/telemetry/register server.js
```

## Deploy a throwaway copy on Fly

The org's apps live on the custom `production` network; flycast IPs must be
allocated on it explicitly (see DEVLOG 2026-08-28):

```sh
fly apps create bh-telemetry-smoke --org beehive-gaming --network production
fly ips allocate-v6 --private --network production -a bh-telemetry-smoke
fly deploy . --ha=false
curl http://bh-telemetry-smoke.flycast/hello
# ...verify in Grafana / VictoriaLogs, then ALWAYS:
fly apps destroy bh-telemetry-smoke --yes
```

Verification queries (Grafana → Explore → VictoriaLogs):

```
_stream:{logger="http", fly.app.name="bh-telemetry-smoke"}
_stream:{logger="http", fly.app.name="bh-telemetry-smoke"} req_body.amount:>100
_stream:{logger="app",  fly.app.name="bh-telemetry-smoke"} level:audit
trace_id:<id from any line> | sort by (_time)
```

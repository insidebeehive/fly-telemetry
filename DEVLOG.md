# Devlog

Decision record for this fork. Newest entries first.

## 2026-09-02 — Blind adversarial QA on 0.1.6 (grade C) -> 9 fixes, shipped 0.1.7

An independent, blindfolded agent tested the PUBLISHED package against the
claimed contract only (no repo access): ~20k requests across express
CJS/ESM/raw-http/Bun with mock OTLP and Logtail endpoints. Held up:
exactly-one-line-per-request under concurrency (6000/6000), the runtime
matrix, crash discipline (one line, exit 1), span-attribute scrubbing,
collector fail-safety, double-activation guard, TS types. Found 1
critical + 4 major + 4 minor. All fixed and re-verified with the QA's own
repros:

1. CRITICAL: JSON bodies over HTTP_LOG_BODY_MAX were logged RAW (truncate
   -> parse fails -> raw fallback) — the everyday case that defeated the
   whole redaction promise. 2. Same for malformed JSON. Fix for both:
   scrubText() in redact.js — key/value regex redaction for JSON-ish and
   urlencoded-ish text + PAN scrub — applied to EVERY raw-text return;
   nothing unparseable is ever logged verbatim now.
3. urlencoded bodies captured verbatim -> per-key URLSearchParams
   redaction (same policy as query strings).
4. Hanging Logtail endpoint leaked sockets/memory without bound (fds
   27->227+, RSS linear in volume) -> AbortSignal.timeout(5s) + in-flight
   cap of 2; verified fds 21->24 under the same flood, stable after.
5. LOG_LEVEL typo (e.g. "banana") silently muted ALL app logging incl.
   audit -> validated loudly, falls back to info. 6/7. Same
   validate-warn-default for HTTP_LOG_PAYLOAD / HTTP_LOG_BODY_MAX /
   HTTP_LOG_SLOW_MS (typos previously turned capture off silently).
8. Errors nested deeper than top-level meta serialized to {} ->
   recursive serializer (depth 4, cycle-safe).
9. Redaction gaps: +ssn, +cvc patterns, +card exact key, and Luhn-valid
   13-19 digit runs redacted as [REDACTED-PAN] anywhere (string leaves
   and raw text) — PANs under innocuous keys are now caught.

Sharp edges addressed: logged header VALUES capped at 512 chars
(amplification vector); SIGTERM flush budget 5s->3s (measured 3.09s vs
5.06s before — now inside Fly's default 5s kill_timeout). Documented, not
changed: *_IGNORE_PATHS replaces defaults; ip = first XFF hop; multipart
never captured; >16KB headers die in Node itself (431, no line).

QA perf numbers (loopback, trivial route): default logging ~+0.7ms/req
(-26% RPS on a no-op route); tracing ~+3.2ms/req regardless of sample
rate (span creation dominates) — fine on real handlers, measure before
enabling tracing on ultra-hot paths. QA verdict on 0.1.6: grade C, "no
for real-money production until bugs 1-5 fixed"; expectation after fixes
B+/A-. 0.1.7 ships the fixes; the QA's exact repro battery is now part
of pre-release verification.

## 2026-09-02 — Logtail, centrally: Vector sink is the org mechanism

User challenge accepted: shipping app logs to BetterStack belongs in the
PIPELINE, not in every app — Vector already sees the whole fleet via NATS
(the superfly/fly-log-shipper pattern; its better-stack.toml provided the
sink shape: dt = del(.timestamp), http sink, bearer auth, json codec).
vector.sh GENERATES /etc/vector/logtail.yaml at boot only when
LOGTAIL_TOKEN is set (CONFIG_DIR loads every file, so an unconditional
${LOGTAIL_TOKEN?} would kill vector when unset): filter .logger == "app"
-> remap dt -> http sink with a disk buffer (drop_newest at 256MiB).
Advantages over the app-side transport: zero app involvement, one config,
disk buffering, crash lines included. The package transport stays for
public/off-Fly consumers and pipeline-independent redundancy — org apps
must not enable both (duplicates). Governance note: the automation's
safety layer hard-blocked the AI from committing this fleet-wide
export pathway; the vector.sh change was reviewed and committed by amit
directly (21333e1). Activation (fly secrets set LOGTAIL_TOKEN ... on
bhgrafana) and the deploy are likewise human steps. Refactored same day
(user suggestion): the sink body moved out of a vector.sh heredoc into
logtail.yaml, staged at /etc/vector-optional/ in the image — vector.sh
just copies it into CONFIG_DIR when LOGTAIL_TOKEN is set, matching
fly-log-shipper's file layout. Staging OUTSIDE /etc/vector is the
load-bearing detail: CONFIG_DIR loads every file it contains. Second
layout pass (user): sink files live in vector-sinks/ (mirroring
fly-log-shipper's vector-configs/sinks/), COPY'd as a directory so
future optional sinks need no Dockerfile change.

## 2026-09-02 — Optional Logtail sink for app logs (user requirement)

LOGTAIL_URL + LOGTAIL_TOKEN (alias LOGTAIL_SOURCE_TOKEN) present -> the
app logger ALSO ships its lines to Logtail/BetterStack. Scope: logger=app
only (audit included); http lines never pass through winston so they are
excluded by construction, per requirement. Hand-rolled ~50-line winston
transport (no new deps; global fetch + winston-transport): 2s/100-line
batches, 1000-line buffer cap that DROPS on outage, fire-and-forget —
Logtail being down can never block a request or crash the app; stdout ->
VictoriaLogs remains the source of truth. Crash lines go to stdout only.
Ships in 0.1.6.

## 2026-09-02 — Dual-publish: npmjs + GitHub Packages (user decision)

The org already hosts @insidebeehive libraries on GitHub Packages and
plans to make GHP primary; until then every telemetry-v* tag publishes to
BOTH registries. npmjs step stays OIDC/tokenless with provenance; the GHP
step uses the workflow's GITHUB_TOKEN (permissions: packages: write),
provenance off (GHP doesn't accept it). Consumer split documented in the
repo README: projects with the @insidebeehive scope mapped to GHP (token
required even for public packages — known GHP limitation, accepted) pick
it up automatically; unmapped projects keep tokenless npmjs. Shipped as
0.1.6.

## 2026-09-01 — HTTP logging defaults: ON everywhere, payload=always (user decision)

Fleet posture for production: any request's line must be self-contained
evidence out of the box. HTTP_LOG now defaults on with no Fly gate (set
HTTP_LOG=off in local dev/test runs) and HTTP_LOG_PAYLOAD defaults always
(errors and off remain opt-downs). Consequence to watch: full-body lines
on every request across the fleet raises log volume beyond the earlier
~3.3GB/day estimate — the logs disk cap (35GiB) drops oldest partitions
sooner, so re-run the retention math from the 08-29 entry once the fleet
adopts. Shipped as 0.1.5.

## 2026-09-01 — Body shape, final: STRING default (user decision after real UI use)

Third and final ruling on http body shape: HTTP_LOG_BODY_MODE defaults to
string again. Seeing object-mode lines in Grafana settled it — every body
key renders as its own row in the log-details field panel, which at
payload=always makes the panel noisy per line, and per-route keys sprawl
the field namespace. String keeps the field set lean; bodies stay
substring-searchable and unpack at query time (LogsQL unpack_json).
object remains the per-app opt-in for stable, hot-queried schemas.
Shipped as 0.1.3.

## 2026-09-01 — Publishing via npm trusted publishing (OIDC), not tokens

Post-release hygiene: 0.1.0 and 0.1.1 were UNPUBLISHED from npm (within the
72h window) because their tarballs carried the pre-cleanup README with
internal endpoints and examples; 0.1.2 — public-safe README, generic
examples, ./package.json export — is the sole public version and the first
shipped fully autonomously (tag -> CI -> OIDC publish with provenance).
Those version numbers are burned (npm never reuses unpublished versions).
NPM_TOKEN Actions secret deleted; no publish credentials exist anywhere.

First publish attempt surfaced npm's 2FA wall (E403 with a non-bypass
token), and the bypass-2FA escape hatch is deprecated — such tokens lose
direct publish around Jan 2027 (github.blog changelog 2026-07-08, flagged
by the user). Workflow reworked to trusted publishing: id-token: write +
npm >= 11.5.1, no NPM_TOKEN secret. Bootstrap constraint: a trusted
publisher attaches to an EXISTING package, so v0.1.0 ships once manually
(npm login + npm publish + OTP), then the npm package settings point at
this repo's publish-telemetry.yml and every later telemetry-v* tag
publishes tokenlessly. The 0.1.0 tag's CI run stays red in history
(auth-only failure; all checks before it passed) — deliberate, not
re-run.

## 2026-09-01 — Package smoke-tested end to end (local + Fly); Bun support; 3 bugs fixed

examples/smoke/ added: express app with CJS + ESM entries, Dockerfile and
fly.toml mirroring the documented integration exactly. Deployed as a
throwaway `bh-telemetry-smoke` on the production network (flycast-only) and
DESTROYED after verification. No other app touched.

- Local matrix (Node 24.20, package installed from the npm-pack tarball —
  tests the publish artifact): inert off-Fly; console-exporter tracing;
  object bodies with nested field redaction; route templates; /health
  ignored; payload policy incl. slow tier; LOG_LEVEL=error with
  logger.audit immunity; {err} serialization; uncaught exception AND
  unhandled rejection -> one structured line then exit (seen in both pretty
  and prod-JSON modes); SIGTERM span flush.
- ESM PILOTED AND PASSED: `--import` register on Node 24 hooks ESM imports
  (spans are route-templated, i.e. express was wrapped through `import`)
  via the IITM message channel; winston injection works in ESM apps. The
  earlier "unpiloted" flag on register.mjs is cleared.
- Fly E2E: NODE_OPTIONS activation from fly.toml; service name auto from
  FLY_APP_NAME; spans landed in VictoriaTraces (Tempo trace-by-id returns
  the POST /bets span); http lines in logger=http with flattened queryable
  body fields (a `req_body.amount:888` filter works) and [REDACTED] values;
  app + audit lines join the http line by trace_id ACROSS streams; /health
  absent everywhere.
- Bugs found by testing, fixed in the package:
  1. getResourceDetectorsFromEnv does not exist in
     auto-instrumentations-node 0.80 (tracing died at boot, fail-safe
     caught it) -> detectors mapped explicitly from @opentelemetry/resources.
  2. sdk-node's spec defaults start OTLP METRICS/LOGS exporters against the
     traces-only endpoint (observed as export failures) -> package defaults
     OTEL_METRICS_EXPORTER=none and OTEL_LOGS_EXPORTER=none when unset.
  3. The http line's `host` field COLLIDED with the Fly envelope's
     machine-host STREAM field, re-keying the http stream by the
     client-controlled Host header (unbounded cardinality risk) -> renamed
     http_host. Only visible by inspecting _stream in the live E2E data.
  4. The SIGTERM/SIGINT span-flush handler never exited: registering a
     signal listener CANCELS default termination, so instrumented processes
     survived kill indefinitely (masked on Fly by the post-grace SIGKILL;
     found because local test processes wouldn't die). Fixed with the
     flush -> stdout-drain -> remove-listener -> re-raise pattern, which
     also keeps an app's own graceful-shutdown handlers working. Verified:
     span flushed AND process exits on SIGTERM.
- Bun 1.4 support (user requirement): the OTel Node SDK wedges Bun's http
  server -> tracing now SKIPS cleanly under Bun with a boot log line;
  Bun's node:http does not publish diagnostics_channel events -> a
  server-wrap fallback is auto-selected (verified: full http lines incl.
  bodies/redaction); winston app+audit loggers work unchanged; trace ids
  arrive via the traceparent header fallback, so Bun services still join
  traces started by Node callers. Bun ignores NODE_OPTIONS: activate via
  CMD `bun --require @insidebeehive/telemetry/register app.ts` or bunfig
  `preload`. Support matrix table added to the package README.
- .NET Core parity (user requirement, design only — build when the first
  .NET service needs it, in its own repo): traces via the official
  OpenTelemetry .NET Automatic Instrumentation (zero-code: CLR profiler env
  vars baked into the image, OTEL_* env identical to Node's). Logging via a
  small Beehive.Telemetry NuGet: ASP.NET Core middleware emitting the SAME
  http.access line shape (field names, redaction policy, payload tiers,
  logger=http) + a console JSON ILogger formatter stamping
  logger=app/service (+ audit as a level-equivalent via a dedicated
  category). Integration target: one builder call, e.g.
  builder.AddBeehiveTelemetry(). Stream contracts and Grafana queries stay
  identical across runtimes.

## 2026-09-01 — Fix: NATS logs source silently dropped scalar-JSON app lines

Observed live in bhgrafana's own vector logs: `Failed deserializing frame
... function call error for "merge" ... expected object, got string`
(suppressed 9+ times — each one a dropped log line). Cause: the logs
source VRL did `. = merge!(., parse_json(.message) ?? {})`; when an app
line is VALID JSON of a non-object (a bare quoted string, number, or
array — e.g. `console.log(JSON.stringify(someString))`), parse_json
succeeds, merge! aborts, and the whole frame is dropped at decode. Fixed
with an is_object guard: non-object lines now flow through with the raw
text left in .message. Takes effect on the next bhgrafana deploy.

## 2026-09-01 — @insidebeehive/telemetry: one package for OTel + HTTP logging

Decision (amends 08-28 and 08-29): the shared telemetry code ships as a
normal npm package apps install (`telemetry/` in this repo, published
PUBLIC to npmjs.org on `telemetry-v*` tags — user decision, MIT; one-time
setup: npm org `insidebeehive` + NPM_TOKEN Actions secret), activated
zero-code via
`NODE_OPTIONS="--import @insidebeehive/telemetry/register"` — one line for
NestJS (CJS) and Remix (ESM) alike — or explicitly via `init()`. This is not
the platform preload returning: the app owns the dependency, its version and
its policy env; only the mechanism is shared. HTTP body logging thereby
becomes platform-provided mechanism with dev-owned policy (fly.toml
HTTP_LOG_* knobs), which amends the 08-28 "in-code, dev-owned" split.

Contents are the retired preload's proven pieces (recovered from f84fa89's
parent) plus fixes found in review:
- ESM support: register.mjs does module.register of the OTel loader hook via
  import-in-the-middle's message channel (the dd-trace bootstrap pattern) —
  required for Remix/Vite server builds, where --require alone silently
  instruments nothing. UNPILOTED — verify on the Remix app before fleet
  rollout; fallback is the documented --experimental-loader flag.
- http-logger: Node >=22 only (pure diagnostics_channel; the Server.emit
  wrap fallback is deleted), content-encoding guard (compressed bodies are
  never decoded, logged as size placeholders), response bodies JSON-only
  (keeps Remix streamed HTML out), express route template in the `route`
  field when req.route exists.
- http-logger emits ONE line per request (user decision, replacing the
  preload's access+payload pair): message type is http.access only; when
  the HTTP_LOG_PAYLOAD policy fires the same line carries redacted headers
  + capped bodies, so a failed transaction's record is self-contained.
  Rationale: transaction apps run HTTP_LOG_PAYLOAD=always, and bare access
  info is not enough there — "when we get screwed, it will be everything
  needed". Enriched lines select with req_body:*; the level split
  (info/debug) is gone with the second line.
- Body shape, revised same day after user challenge: default is now OBJECT
  (HTTP_LOG_BODY_MODE=object) — parsed+redacted JSON bodies land as nested
  fields, so `req_body.amount:>100` filters directly. The original
  string-default rationale (ingest-time field explosion) was checked against
  the VictoriaLogs data model and largely dissolved: VL flattens dicts with
  dots but CONVERTS ARRAYS TO STRINGS at ingest, so per-index explosion
  can't happen, and the fleet's body schemas are shallow (1-2 levels, per
  user). Truncated/non-JSON/compressed bodies remain strings/placeholders
  in either mode (field queries skip those rows; `payload:true` marks
  enriched lines mode-independently). HTTP_LOG_BODY_MODE=string is the
  per-app escape hatch (query via unpack_json then). VERIFIED against live
  logs (bhgrafana IS reachable from the dev runner — an earlier silent-curl
  check said otherwise): logger=http stream carries softstudio-core (~694k
  lines) + core-stage; sampled reqBody/respBody are mostly depth-1 objects,
  one payload type is depth-4 with arrays — fine, VL stringifies arrays.
  Revisit trigger stands: per-block field counts or ingest RAM degrading on
  VictoriaLogs self-monitoring. Also adopted from the legacy logger:
  trace_sampled on http lines (whether the trace_id resolves to stored
  spans). Migration note: legacy field names (reqBody/respBody/statusCode/
  respTime) differ from the package's (req_body/res_body/status/
  duration_ms) — update saved queries per app at switch-over.
- Package also exports a zero-config app `logger` (winston 3.19.0 dep):
  defaultMeta {logger: "app", service}, JSON on Fly/prod, pretty locally,
  LOG_LEVEL env, trace_id injected inside requests by the winston
  instrumentation. Built lazily on first use so winston is required after
  the OTel require hook registers (protects the programmatic-init path;
  pino-only apps never load winston). Completes the stream convention from
  the 08-28 partition entry: logger=http | app | (absent).
- App logger also owns crash logging: handleExceptions/handleRejections on
  the Console transport turn uncaught exceptions and unhandled rejections
  into ONE structured line (raw stderr stacks get split per-line by the
  log stream into unqueryable fragments), then the process exits as before
  (winston exitOnError default; Fly restarts). init() force-builds the
  logger so handlers cover boot crashes — the laziness now only serves the
  import-without-activation case. Nested Errors in meta are serialised
  (message+stack+code) by a format step; plain winston logs them as {}.
  Recommended pattern: logger.error("context", { err, ...fields }).
- TypeScript: hand-written index.d.ts / register.d.ts shipped (logger typed
  as winston's own Logger — winston bundles its types), wired via "types"
  conditions in the exports map + top-level "types" for legacy
  moduleResolution. No build step; the package stays plain JS.
- Audit events: logger.audit(...) — a custom winston LEVEL at priority 0
  (above error), not a separate stream (user decision, revised same day
  from a briefly-added `audit` export/stream). Priority 0 means LOG_LEVEL
  can never silence an audit event; lines stay in logger=app and are
  selected with `level:audit`. Stream convention stays http | app |
  (absent). Typed via AppLogger interface (Logger + audit:
  LeveledLogMethod). Durability stance recorded: the log pipeline is
  best-effort and retention-bounded — DB remains the system of record for
  regulatory audit; this level is the queryable/correlatable copy.
- tracing: pino trace_id injection enabled alongside winston (api-tester
  pilot lesson). NO baked endpoint (user decision, same day — the collector
  URL is deployment config): apps always set OTEL_EXPORTER_OTLP_ENDPOINT,
  unset = tracing off, which is also what keeps local/CI clean. Package
  defaults are written into the STANDARD env vars only when unset, so
  fly.toml overrides behave exactly like stock OTel:
  OTEL_TRACES_EXPORTER=otlp, OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf,
  OTEL_NODE_RESOURCE_DETECTORS=env,host,os,process, and
  OTEL_RESOURCE_ATTRIBUTES gains cloud.provider=fly_io (on Fly),
  cloud.region=$FLY_REGION|auto, service.instance.id=$FLY_MACHINE_ID|NA,
  service.version=$FLY_IMAGE_REF|NA, plus legacy fly.region. Missing Fly
  vars produce ONE console.warn at registration (never per request), each
  line naming the variable, what it feeds, the fallback in effect and the
  exact OTEL_* override to set; overridden vars are not warned about.
  service.name resolution:
  OTEL_SERVICE_NAME > FLY_APP_NAME > app package.json name (shared with the
  http logger so lines and spans agree).
- http logger auto-off outside Fly (HTTP_LOG=on forces it on locally);
  kill switches OTEL_SDK_DISABLED and HTTP_LOG=off unchanged.

Known limits accepted: Remix logs raw redacted paths (no route id at the
transport layer); payload capture is tied to neither sampling nor winston —
it is the 100% record. Rollout checklist per app: remove any in-repo OTel
bootstrap (double-instrumentation), disable morgan/Nest access-log
interceptors (duplicate logger=http lines), CMD must exec node directly.

## 2026-08-29 — Preload approach cancelled; pilot retired

Decision: OTel instrumentation goes IN-CODE per service (the core PR #1846
pattern: app-owned bootstrap + sampler), not via the platform preload. The
`telemetry-preload/` package and the api-tester pilot are removed from the
branch (recoverable at f84fa89 / 6dbdf28); the pilot's stopped machine on
api-tester is destroyed. What carries forward from that work regardless:
bo's redaction/scrubbing patterns now proven portable, the service-name =
FLY_APP_NAME convention worth adopting, and the pilot lessons devlogged
earlier. The Service Observability dashboard is marked disposable pending
the team's rebuild; the datasource wiring (Jaeger search view of
VictoriaTraces + bidirectional trace<->log links) stays — any dashboard
needs it.

## 2026-08-29 — Deferred: URL query-param redaction in core's HTTP logs

The stage-lineage HTTP logger records raw query strings (main-ai-v2 has
`redactUrl`; stage-ai-v2 doesn't). Better long-term shape agreed: split URL
into path + `query` object passed through the existing `redact()` — one policy
for headers/bodies/params, individually queryable fields. Deferred by
decision (internal flycast access only; accepted). Revisit triggers: http
logger reaching main-ai-v2/production, mesh access widening beyond eng, or
Grafana auth work. Note: Logtail transport means log lines also leave the
private network to BetterStack.

## 2026-08-29 — Retention policy set; volume 50 → 300 GB

Requirement: fleet-wide logs incl. HTTP bodies for 1–2 months, metrics max 2
weeks, traces (10% sampled) 2 weeks. Configured in start.sh:
logs `-retentionPeriod 60d` (intent), metrics `14d` (down from the 1-month
default), traces `14d`. Volume deliberately NOT extended yet (user decision:
evaluate first) — so disk caps are sized to the current 50 GB volume: logs
35GiB, traces 8GiB; oldest partitions drop at the cap instead of the disk
filling. At measured rates (~3.3 GB/day compressed) logs hit their cap around
day 10–11 — that is the evaluation point. Full 60d needs ≈ 250–450 GB
(volumes extend online, never shrink); raise the caps together with the
volume when the time comes.

## 2026-08-28 — Final architecture split: traces zero-code, HTTP logging in-code

- **Preload (platform-owned, zero app code)**: OTel traces at 10%, traceparent
  propagation on 100%, winston trace_id injection. Spans carry method/route/
  status/duration for sampled requests — never bodies (OTel excludes payloads
  by design).
- **HTTP request/response logging (dev-owned, in code)**: each service logs
  req/res incl. bodies at INFO level through its normal winston logger →
  stdout → NATS → VictoriaLogs. The preload's winston injection stamps those
  lines with trace_id automatically, which is the join key between the 100%
  log record and the 10% span waterfall.
- Reference implementation for the in-code logger: the interceptor pattern +
  bo's `redact.util.ts` (header allowlist, key-normalised redaction, query
  redaction, body caps). Route template not raw URL; JSON console format in
  prod (bo's nestLike pretty-print defeats field indexing).

## 2026-08-28 — telemetry-preload built; api-tester pilot verified end to end

- **Scope decision: traces only.** The preload (`telemetry-preload/`) does OTel
  auto-instrumentation — traceparent on 100%, spans at 0.1 default, winston
  trace_id injection — and nothing else. An HTTP request/response payload
  logger was prototyped and removed at the same day's decision (git history
  has it); HTTP payload capture will be decided separately. Note: OTel has no
  native body capture by design; spans natively carry method/route/status/
  duration for sampled requests (headers opt-in via env, never bodies).
- **Pilot proof (api-tester)**: wrapped the app's EXISTING image
  (`FROM registry.fly.io/api-tester:sha-…` + COPY preload) with zero source
  changes; spans arrived in VictoriaTraces with correct service name (from
  FLY_APP_NAME), fly.region, environment. Query path verified via the Jaeger
  API (`/select/jaeger/api/traces?service=api-tester&start=…&end=…` — the
  Tempo `api/search` endpoint returns empty even for existing traces in
  v0.10.0; trace-by-id and Jaeger search work).
- **Pilot lessons that will matter for real rollouts:**
  - Preload via image CMD `--require` on the SERVER process, not NODE_OPTIONS,
    when images boot through npm/nest/tsc — instrumenting the whole toolchain
    starved a 512MB machine into a wedge (1GB + NODE_OPTIONS also works).
  - api-tester runs Node 18 (nixpacks) — wrap-mode auto-detect worked; its
    logs are pino/Fastify, so for pino apps enable instrumentation-pino
    (the preload currently enables winston only, matching bo/core).
  - Machines created by flydeck/machines-API lack fly_process_group metadata;
    `fly deploy` won't update them — it creates parallel machines instead.
    Long-staged secrets then apply to the new machines only.
  - Cross-network reach for the pilot used a TEMPORARY default-network flycast
    IP on bhgrafana (`fly ips allocate-v6 --private` with no --network lands on
    default; `--network <name>` only accepts named custom networks). Released
    after the pilot: bhgrafana is production-network-only again. Captured
    traces remain queryable; the stopped pilot machine (d897352c3166e8) can be
    restarted for another round, but its exporter needs the bridge IP back.

## 2026-08-28 — Moved to the org's `production` network (destroy + recreate)

- **The org's apps live on a custom Fly network named `production`** (`fdaa:74:505b::/48`).
  Creating bhgrafana without `--network` put it on the org *default* network
  (`fdaa:c:458c::/48`) — networks are mutually isolated (no DNS, no routing), so the team's
  mesh couldn't reach the UI and services could never have sent traces to it. Networks are
  fixed at app creation → destroyed and recreated with
  `fly apps create bhgrafana --org beehive-gaming --network production`.
  **Always check the network column in `fly apps list` before creating apps in this org.**
- **`fly deploy --flycast` allocates its private IP on the DEFAULT network** even when the
  app is on a custom network. Fix: `fly ips release <ip>`, then
  `fly ips allocate-v6 --private --network production -a bhgrafana`. Verify the flycast
  prefix matches the machines' 6PN prefix in `fly ips list`.
- ACCESS_TOKEN was carried over in-memory (same token, never displayed). The old volume's
  ~2h of telemetry was not preserved — Fly volumes are app-bound (no cross-app
  detach/attach); if data ever matters during a future move, evaluate snapshot-restore
  first (daily snapshots are enabled).
- Re-verified after the move: ingestion resumed immediately (~320k log rows/5min,
  ~17.5k `fly_*` series), UI 200 via `fdaa:74:505b:0:1::a`, `bo-api-casino.flycast`
  resolves from inside the app (same-network proof), OTLP smoke span ingested through
  `bhgrafana.flycast:10428` and read back via the Tempo API.

## 2026-08-28 — First deploy findings (bhgrafana, sin, beehive-gaming)

- **NATS "authorization violation" root cause: the floating `timberio/vector:latest` tag.**
  The unpinned tag pulled vector 0.58.0 (built two days before our deploy), whose NATS client
  is rejected by Fly's NATS proxy with credentials that are provably valid — a raw NATS
  handshake from inside the machine (`CONNECT`/`SUB logs.>`) authenticated and streamed org
  logs with the exact same user/token. Verified in-machine with the template's own config:
  vector **0.46.1 works** (upstream-era version), **0.57.0 and 0.58.0 fail**. Pinned
  `timberio/vector:0.46.1-distroless-static`; bump only with a changelog read and an
  in-machine NATS test. (The token itself was fine all along — `readonly` org tokens are the
  documented type for the platform streams.)
- **Vector exits permanently when a source fails at topology build** — and since Grafana is
  the container's foreground process, the machine looks healthy while ingestion is dead.
  Fix: `vector.sh` now supervises vector in a retry loop.
- **Grafana `main` image clamps anonymous access to Viewer.** Grafana 12.2 logs
  `auth.anonymous.org_role is deprecated, only viewer role is supported` — the template's
  anonymous-Admin assumption no longer holds. Dashboards/datasources are file-provisioned so
  day-to-day viewing and Explore still work; interactive dashboard *editing* needs an admin
  login (`GF_SECURITY_ADMIN_PASSWORD`) or pinning an older Grafana. Decide when dashboard
  work starts in earnest.
- Volume `data` (10GB, sin) was created with scheduled daily snapshots on by default
  (5-day retention) — plan step 2.4 satisfied without extra work.
- **Grafana moved from port 3000 to 80** (grafana.ini `http_port`, fly.toml `internal_port`,
  vector self-scrape endpoint): the UI is port-free on 6PN (`http://bhgrafana.internal/`) and
  the Flycast mapping is a same-number 80 → 80, ending the 3000-vs-80 confusion. Laptop access:
  `fly proxy 3000:80 -a bhgrafana` → http://localhost:3000 (binding local 80 would need sudo).
- **Verified end-to-end after the vector pin**: ~155k log rows/5min in VictoriaLogs and
  ~16.6k `fly_*` platform metric series in VictoriaMetrics (org streams flowing); self-scrape
  series present for metrics/logs/traces/grafana (note: Grafana 12 no longer exposes
  `process_resident_memory_bytes` — use its `go_*`/`grafana_*` metrics in the self-monitoring
  dashboard); OTLP smoke span POSTed to `http://bhgrafana.flycast:10428/insert/opentelemetry/v1/traces`
  returned 200 and was read back via both `/select/tempo/api/traces/<id>` and the Jaeger API
  (allow a few seconds of ingestion-visibility lag before querying). Only IP on the app:
  private flycast. Access until the Netbird peer exists: `fly proxy 3000 -a bhgrafana` →
  http://localhost:3000 (proxies to the machine's Grafana port directly; note `fly proxy`
  cannot resolve `.flycast` names from outside the network, and the machine itself has
  nothing on port 80 — that port only exists on the flycast proxy address).

## 2026-08-28 — Add VictoriaTraces (traces phase 1)

### Version pins
- **VictoriaTraces v0.10.0** (`victoriametrics/victoria-traces:v0.10.0`). Latest at pin time
  was v0.11.0 (2026-08-14) but it is flagged pre-release upstream; v0.10.0 (2026-07-22) is the
  release marked *Latest*. VictoriaTraces is **pre-GA**: read the changelog before every bump,
  and treat trace data as disposable. The Tempo query endpoint requires ≥ v0.9.4.
- VictoriaMetrics v1.118.0 and VictoriaLogs v1.22.2 pins inherited from upstream, unchanged.

### Topology: single machine, single process group
The original plan split the app into `collect` (Vector + Victoria stores) and `grafana`
process groups. Decided against it and kept the template's single-machine layout: if any
collector is down, Grafana is degraded anyway, and one machine is less to operate. Everything
talks over `localhost`, so the Grafana datasources stay file-provisioned and unchanged apart
from the new traces entry. Consequence kept in mind: one volume's 64 MiB/s bandwidth cap is
shared by metrics+logs+traces, and one `fly deploy` rolls everything together. The vm size in
`fly.toml` is `shared-cpu-8x` / 2GB (CPU-biased pick; app name `bhgrafana`). 2GB plus 1GB swap
is shared by all five processes, so memory is the first thing to watch on the self-monitoring
scrape below — that's the tripwire for resizing or splitting later.

### Build change (important)
`fly.toml` no longer uses `build.image = "flyio/fly-telemetry"` — the upstream prebuilt image
doesn't contain VictoriaTraces. Deploys now build the local `Dockerfile`, which means the
`dashboards/` **git submodule must be checked out** at deploy time
(`git clone --recurse-submodules` or `git submodule update --init`).

### Trace ingest & query wiring
- `start.sh` launches `/victoria-traces-prod` with `-storageDataPath /data/traces`
  (matches the `/data/metrics`, `/data/logs` convention — the plan's `/data/victoria-traces`
  path was adjusted to fit), `-retentionPeriod 14d` (default is 7d; 14d is enough to debug
  across a sprint boundary while keeping the disposable-data stance), `-httpListenAddr :10428`.
  `-envflag.enable` + `enableTCP6` makes it listen on IPv6, required for Fly private networking.
- OTLP HTTP ingest exposed via Flycast: raw TCP service on port 10428 in `fly.toml`.
  Services set `OTEL_EXPORTER_OTLP_ENDPOINT=http://<app>.flycast:10428/insert/opentelemetry`
  (base path; SDKs append `/v1/traces`) with `http/protobuf`.
- Grafana queries traces through the core **Tempo** datasource type pointed at
  `http://localhost:10428/select/tempo` (uid `victoria_traces`, for later trace↔logs links).
  Keeping the Tempo-compatible endpoint makes a future swap to actual Tempo a datasource-URL
  change, which is the cheap exit if pre-GA instability bites.

### No OTel collector (deliberate)
Apps export OTLP straight to VictoriaTraces. A collector adds a hop and an ops surface with no
current payoff; it gets reconsidered only if we need sampling, fan-out to a second backend, or
dual-write HA.

### Non-HA (deliberate)
Single instance, no replication. `fly-metrics.net` and `fly logs` remain the platform fallback
during outages, plus daily volume snapshots on the data volume. Trace data is explicitly
disposable at 14d retention.

### Self-monitoring
Vector now also scrapes `localhost:10428/metrics` (VictoriaTraces, tagged `process=traces`) and
`localhost:3000/metrics` (Grafana, tagged `process=grafana`) alongside VM/VLogs. That feeds the
planned self-monitoring dashboard (`process_resident_memory_bytes` per component) — the trigger
for future "resize memory" / "split query load" decisions. If the shared volume ever fills,
`-retention.maxDiskSpaceUsageBytes` on victoria-traces is the lever to cap trace disk usage.

### Security posture (unchanged)
Grafana stays anonymous-admin; the auth boundary is network-level (Flycast only, no public
IPs — verify with `fly ips list` after every launch/config change). Grafana auth moves out of
the deferred list if mesh access ever widens beyond the eng team.

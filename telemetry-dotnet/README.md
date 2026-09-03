# Beehive.Telemetry

One-line observability for ASP.NET Core services: OpenTelemetry **traces**, structured
**HTTP request logging**, and a preconfigured **app logger** with an audit level — one
package, one line of code.

Built for stacks that collect stdout JSON logs (Vector → VictoriaLogs, Loki, Datadog, or
any JSON-aware log pipeline) and OTLP traces. Auto-configures on platforms that expose the
usual app/region/machine environment variables; works anywhere .NET 8 runs.

- **Traces** — OpenTelemetry ASP.NET Core + HttpClient instrumentation, parent-based
  sampling (10% default), OTLP export to the endpoint you configure. Off until you
  configure one, so dev and CI emit no spans.
- **HTTP logging** — exactly one `http.access` JSON line per request (100%: method /
  path / route / url / http_host / status / duration_ms / ip / trace_id). When the payload
  policy fires, the **same line** also carries redacted headers and capped bodies, so a
  failed transaction's record is self-contained evidence. Every line has `logger=http` for
  stream-level separation from app logs.
- **App logger** — the standard `ILogger<T>`, formatted as JSON in production and
  pretty-printed locally, with `logger=app`, `service` and `trace_id` stamped on every
  line. Includes an `Audit` level that no log-level setting can silence.

Everything is fail-safe: a telemetry failure is reported and swallowed, never propagated.
Observability must not be the reason a service fails to boot.

## Setup

```sh
dotnet add package Beehive.Telemetry
```

One line in `Program.cs`:

```csharp
using Beehive.Telemetry;

var builder = WebApplication.CreateBuilder(args);
builder.AddBeehiveTelemetry();          // <- that is the whole integration

var app = builder.Build();
app.MapGet("/hello", () => new { hello = "world" });
app.Run();
```

There is **no `app.UseSomething()`** to add: the access-log middleware installs itself at
the very front of the pipeline through an `IStartupFilter`, so it times the whole request
and nothing upstream can swallow a request unlogged. Calling `AddBeehiveTelemetry()` twice
is a no-op. An `IHostApplicationBuilder` overload is available for non-web hosts (tracing
and app logging only apply there).

To turn tracing on, set an endpoint — this is deployment config, never a code default:

```sh
OTEL_EXPORTER_OTLP_ENDPOINT="http://your-collector:4318"   # base path; /v1/traces is appended
```

Verify: boot logs show `[telemetry] tracing enabled -> …` and
`[telemetry] http logger enabled …`; your log store receives `logger=http` and
`logger=app` JSON lines; your tracing UI shows spans for the service.

**Migrating a service that already has its own OpenTelemetry bootstrap:** remove it when
enabling this package, or the process is instrumented twice. **This replaces per-app access
logging** — ASP.NET Core's own `Microsoft.AspNetCore.Hosting` "Request starting/finished"
lines are a second access log AND print the raw query string (a token in a URL would bypass
this package's redaction through them). `AddBeehiveTelemetry()` therefore defaults the
`Microsoft.AspNetCore` category to `Warning`, so a zero-config app is safe without an
`appsettings.json`. Re-enable those lines deliberately with
`builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Information)` AFTER
`AddBeehiveTelemetry()` if you want them back.

## Zero-code activation

The one-line `AddBeehiveTelemetry()` above is the **recommended** path: explicit, greppable,
and the only option if you also want to adjust logging or re-enable a framework log in
`Program.cs`. But when you cannot — or would rather not — edit the entrypoint (a base image, a
third-party binary, a container where you only control the environment), the package activates
with **no code at all** through the standard ASP.NET Core hosting-startup hook:

```sh
ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=Beehive.Telemetry
OTEL_EXPORTER_OTLP_ENDPOINT="http://your-collector:4318"   # tracing is still opt-in, as above
```

This is the .NET analog of the npm package's
`NODE_OPTIONS="--import @insidebeehive/telemetry/register"`: it wires the exact same three
concerns — tracing, the `http.access` middleware and app logging + crash handlers — as the
one-line call. On Fly.io that is the `[env]` block of `fly.toml`:

```toml
[env]
  ASPNETCORE_HOSTINGSTARTUPASSEMBLIES = "Beehive.Telemetry"
  OTEL_EXPORTER_OTLP_ENDPOINT = "http://your-collector.internal:4318"
```

Activation is **opt-in by that variable only**: a `PackageReference` on its own does nothing
without `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` set — there is no surprise instrumentation from
merely referencing the package. And the two styles are **safe together**: an app that sets the
variable AND calls `AddBeehiveTelemetry()` is instrumented **exactly once** (whichever runs
first wins; the other is a no-op — one banner, one line per request, one set of crash
handlers), so you can bake the variable into a base image and still keep the explicit call in
code.

## HTTP access logging

One line per request, on stdout, written directly (never through `ILogger`, so `LOG_LEVEL`
cannot silence the 100% record and the console formatter cannot recurse):

```json
{"level":"info","message":"http.access","ts":"2026-09-01T10:24:03.512Z","logger":"http","service":"my-api","runtime":"dotnet","method":"POST","path":"/api/orders","route":"/api/orders/{id}","url":"/api/orders?page=2&token=[REDACTED]","http_host":"api.example.com","status":201,"duration_ms":42.7,"ip":"203.0.113.7","trace_id":"4bf92f3577b34da6a3ce929d0e0e4736","span_id":"00f067aa0ba902b7","trace_sampled":true,"res_bytes":86}
```

- `route` is the endpoint routing pattern, which keeps path cardinality sane in queries;
  it is omitted when no endpoint matched.
- `ip` is the first `X-Forwarded-For` hop, else the remote address.
- `trace_sampled` says whether this `trace_id` will resolve to stored spans (head sampling
  keeps ~10%) — it saves chasing ids that were never exported. Ids come from the ambient
  `Activity`, falling back to the caller's `traceparent` header.
- A client that disconnects gets `"status":499,"aborted":true`.
- An unhandled exception is logged (status 500, or the real status if the response had
  already started) and then **rethrown** — the middleware is transparent.
- `http_host` (not `host`) carries the Host header, because a plain `host` field collides
  with the machine-host stream field of hosted log pipelines, and the Host header is
  client-controlled.
- `runtime` is the host runtime — `node` | `bun` | `dotnet` across the Beehive telemetry
  family, always `dotnet` here — so a mixed fleet's lines stay distinguishable. It is stamped
  on both the `http.access` line and every `logger=app` line.

When the payload policy fires, the **same** line also carries:

```json
"payload":true,"req_headers":{…},"req_body":"…","req_body_truncated":true,"res_headers":{…},"res_body":"…"
```

Select enriched lines with `payload:true` (or `req_body:*`). Request bodies are read with
buffering enabled and rewound, so model binding still sees them. Response bodies pass
through a counting stream. Bodies that cannot be scrubbed safely are never rendered — they
become a size placeholder instead: `[gzip 1234 bytes]`, `[multipart/form-data 1234 bytes]`,
`[application/json utf-16le 64 bytes]`.

An `application/x-www-form-urlencoded` request body is **decoded into a key/value object**
and rendered exactly like a JSON body — spaces are spaces, no `%XX` escapes — so
`user=amit+kumar&password=secret` reads as `{"user":"amit kumar","password":"[REDACTED]"}`,
identical to the equivalent JSON body. Repeated keys collapse to an array so nothing is
lost, and `HTTP_LOG_BODY_MODE` applies the same way (one JSON string, or nested fields in
`object` mode).

### Environment knobs

Every value is case-insensitive, and every invalid value falls back **loudly** to the safe
default — a typo must never silently disable evidence capture.

| Variable | Default | Meaning |
| --- | --- | --- |
| `HTTP_LOG` | `on` | Master switch. `off` skips the middleware entirely. |
| `HTTP_LOG_PAYLOAD` | `always` | `always` \| `errors` \| `off`. `errors` = status ≥ 400 or slow. |
| `HTTP_LOG_SLOW_MS` | `1000` | The "slow" threshold for the `errors` tier. |
| `HTTP_LOG_BODY_MAX` | `4096` | Bytes kept per body. |
| `HTTP_LOG_BODY_MODE` | `string` | `string`: bodies are one JSON-encoded string field. `object`: parsed bodies land as nested fields. |
| `HTTP_LOG_PAYLOAD_ROUTES` | — | Comma-separated path prefixes that always get payloads. |
| `HTTP_LOG_IGNORE_PATHS` | `/,/health,/healthz,/favicon.ico` | Paths that emit nothing. An entry ending in `/` (except bare `/`) is a subtree prefix; everything else matches exactly. |
| `HTTP_LOG_IGNORE_EXTENSIONS` | `js,mjs,cjs,css,map,ico,png,jpg,jpeg,gif,svg,webp,avif,woff,woff2,ttf,eot` | File extensions whose requests emit nothing (matched on the last path segment, case-insensitive, leading dots ignored) — front-end static assets. `off`/`none` logs assets too. Business downloads (`pdf`, `csv`, `xlsx`, `zip`) are deliberately **not** in the default. |

## Redaction policy

**Logs are evidence.** Business data — amounts, ids, card/account numbers, transaction
refs, paths — is logged **verbatim**. Redaction is reserved for material that grants
access: passwords, OTPs, PINs, session ids and tokens, API keys, secrets, private/access
keys, credentials and webhook signatures. There is deliberately no Luhn/PAN scrubbing:
false positives redact the very numeric refs these logs exist to keep.

The test is on the key NAME, after stripping non-alphanumerics — so `api_key`,
`x-api-key` and `apiKey` all match, while `spinCount`, `cacheKey`, `design` and `keyword`
deliberately do not. It applies to JSON bodies (recursively, with a depth cap), urlencoded
bodies, query strings, and — as a backstop for text that would not parse, including a body
sliced by the byte cap — raw text.

`Authorization` and `Cookie` are structurally un-loggable: headers are an **allowlist**
(`host`, `content-type`, `content-length`, `user-agent`, `referer`, `origin`,
`accept-language`, `x-forwarded-for`, `userid`, `operatorid`, `traceparent`), so they are
simply never picked. A denylist has to be extended every time an auth header is added and
is silently wrong in the window before someone notices.

The same policy is applied to spans by a processor that runs before export, so a span's
`url.query` and an `http.access` line's `url` agree. This is also why the package sets
`OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION=true` by default: the
instrumentation's own default replaces *every* query value with `Redacted`, including the
harmless ones. Set it to `false` to get the blanket behaviour back.

The policy is public API, so you can apply it yourself:

```csharp
Redaction.IsSensitiveKey("x-api-key");   // true
Redaction.RedactUrl("/pay?token=t&page=2");   // "/pay?token=[REDACTED]&page=2"
Redaction.ScrubText(untrustedText);
```

## App logging

Inject `ILogger<T>` as usual — there is no package-specific logger type:

```csharp
app.MapPost("/orders", (ILogger<Program> logger, Order order) =>
{
    logger.LogInformation("order placed {OrderId} {Amount}", order.Id, order.Amount);
    logger.Audit("order.settled", new { actor = "cron", orderId = order.Id, order.Amount });
    return Results.Ok();
});
```

In production each call is one JSON line on stdout:

```json
{"level":"info","message":"order placed o_991 250","timestamp":"2026-09-01T10:24:03.512Z","logger":"app","service":"my-api","runtime":"dotnet","trace_id":"4bf92f3577b34da6a3ce929d0e0e4736","span_id":"00f067aa0ba902b7","OrderId":"o_991","Amount":250,"category":"Program"}
```

Locally it pretty-prints with colours instead. Notes:

- Level names are lower-cased and abbreviated: `trace`, `debug`, `info`, `warn`, `error`,
  `critical`, plus `audit`.
- Message-template values and logging-scope values are flattened as top-level fields.
- Exceptions become `"err":{"message":"…","stack":"…"}` with the full stack, including
  inner exceptions — an `Exception` passed as a state value is serialized the same way.
- `trace_id`/`span_id` appear automatically on lines logged inside a request — the pivot
  between an app log, its `http.access` line and its spans.
- `LOG_LEVEL` (`trace|debug|info|warn|error`, default `info`) sets the minimum level. It
  gates **only** app logging; the HTTP logger writes to stdout directly and always emits.
  There is no `none`/`off`: silencing the logger would also silence audit events, so those
  values are rejected like any other invalid value.
- `LOG_FORMAT` (`json|pretty`) overrides the automatic choice, which is JSON when a
  platform app name is set or the environment is Production.
- **`Audit` can never be silenced.** It logs at `Critical`, which passes every accepted
  `LOG_LEVEL`, and the formatter renders the level as `audit`. Audit lines stay in
  `logger=app` and are selected with `level:audit`.
- **The app logger logs exactly what you pass it.** The automatic redaction applies to HTTP
  capture, query strings and spans — not to your own message-template values. Do not put
  passwords, tokens or API keys in log fields.
- **Unhandled exceptions and unobserved task exceptions** are captured as one structured
  JSON line (same shape, stack included) written straight to stdout, and are then left
  alone: the process dies exactly as it would have, so your platform restarts it.

## Tracing

Off until `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` or `OTEL_EXPORTER_OTLP_ENDPOINT` is set (or
when `OTEL_SDK_DISABLED=true`); no SDK objects are constructed at all in that case.

When enabled, the package writes its defaults into the **standard** `OTEL_*` variables when
you have not set them, so overriding any of them behaves exactly like stock OpenTelemetry:

- `OTEL_EXPORTER_OTLP_PROTOCOL` defaults to `http/protobuf` (the SDK's own default is gRPC).
- `service.name` resolves as `OTEL_SERVICE_NAME` > platform app name > entry assembly name
  > `unknown-service`, and is the same value every log line's `service` field carries.
- Resource attributes `cloud.provider`, `cloud.region`, `service.instance.id`,
  `service.version` and `deployment.environment.name` are composed into
  `OTEL_RESOURCE_ATTRIBUTES` — only keys you did not set. When the platform variables are
  missing, one startup warning lists what is missing and the exact line to paste.
- Sampling is `ParentBased(TraceIdRatioBased(OTEL_TRACES_SAMPLER_ARG))`, default `0.1`:
  spans are the code-level 10%, while the 100% "which request went where and responded how"
  record is the `http.access` line's job. Parent-based means a sampled request keeps its
  complete journey across services.
- `OTEL_IGNORE_PATHS` (same default and match semantics as `HTTP_LOG_IGNORE_PATHS`) drops
  health-check spans before they are created.
- The last span batch is flushed on shutdown, then the provider is shut down, within a
  combined ~3s budget so an unreachable collector cannot push shutdown past a platform's
  kill timeout (Fly's default is 5s); a deploy still keeps the traces for whatever was in
  flight.

Only **traces** are exported. Metrics and logs exporters are deliberately not enabled.

## Compatibility

Targets `net8.0` and requires ASP.NET Core. Dependencies are the OpenTelemetry SDK,
instrumentation and OTLP exporter; JSON uses the in-box `System.Text.Json`.

## Licence

MIT.

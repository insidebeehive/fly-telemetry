# Changelog

All notable changes to `Beehive.Telemetry` (.NET). Format based on
[Keep a Changelog](https://keepachangelog.com/). Versioning is
[SemVer](https://semver.org/). This is the .NET twin of the npm package
`@insidebeehive/telemetry`; the log line shape, env knobs and redaction policy
are kept in parity across both.

## [0.1.3] - 2026-09-17

### Added
- **`HTTP_LOG_RES_BODY_IGNORE_ROUTES`** — paths whose **response body** is never
  captured, while the rest of the line is kept in full: access fields, request
  body, redacted headers, `res_bytes` (the true wire size), `res_headers`, and
  an explicit `res_body_suppressed: true` marker. Matching mirrors
  `HTTP_LOG_IGNORE_PATHS` — exact unless the entry ends in `/` (subtree); a bare
  `/` stays exact so it cannot silently blank every response body in the app.

  A denylist, because neither existing knob can express "log everything here
  except the response body": `HTTP_LOG_PAYLOAD_ROUTES` is an allowlist and
  `HTTP_LOG_IGNORE_PATHS` drops the whole line, request body included.

  The decision is made before the response stream is wrapped, so a suppressed
  route passes `keep: 0` to `ResponseCaptureStream` and allocates no capture
  buffer at all — it costs nothing in memory either, not merely nothing on disk.
  Default empty; unset changes nothing.

  *(Parity with npm `@insidebeehive/telemetry` 0.4.0.)*

## [0.1.2] - 2026-09-03

### Added
- **Static assets skipped by default** via `HTTP_LOG_IGNORE_EXTENSIONS`
  (default `js,mjs,cjs,css,map,ico,png,jpg,jpeg,gif,svg,webp,avif,woff,woff2,ttf,eot`;
  `off`/`none` disables). Business downloads (`pdf`,`csv`,`xlsx`,`zip`) are
  deliberately not in the default.
- Form bodies (`application/x-www-form-urlencoded`) are **decoded** into a key/value
  object rendered like JSON bodies (`user=amit+kumar&password=secret` →
  `{"user":"amit kumar","password":"[REDACTED]"}`); per-key redaction, repeated keys
  collapse to an array, honors `HTTP_LOG_BODY_MODE`.

  *(Parity with npm `@insidebeehive/telemetry` 0.3.0.)*

## [0.1.1] - 2026-09-03

### Added
- **Zero-code activation** — the .NET analog of Node's `NODE_OPTIONS`. Set
  `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=Beehive.Telemetry` and the app is fully
  instrumented with no `Program.cs` change (an `IHostingStartup`). Opt-in by that env
  var only; `builder.AddBeehiveTelemetry()` remains the recommended path and the two
  are idempotent together.
- `runtime` field (`dotnet`) on every `http.access` and app line, matching the npm
  package's `node`/`bun`.

## [0.1.0] - 2026-09-03

### Added
- Initial release. One line — `builder.AddBeehiveTelemetry()` — wires all of:
  - the `http.access` middleware (inserted first automatically via an
    `IStartupFilter`, no `app.Use…` needed) emitting one JSON line per request with
    field names byte-for-byte matching the npm package (`ts`, `http_host`,
    `duration_ms`, `logger=http`), payload enrichment, `499`+`aborted` on client
    abort, and a line-plus-rethrow on unhandled exceptions;
  - OpenTelemetry tracing (parent-based sampling, OTLP `http/protobuf`, gated on
    `OTEL_EXPORTER_OTLP_ENDPOINT`, Fly resource attributes, a scrubbing `Activity`
    processor);
  - console logging — JSON in production, pretty locally (`logger=app`,
    `service`, trace ids) — with a `logger.Audit(...)` extension no `LOG_LEVEL` can
    silence, and crash handlers.
- Evidence-first redaction policy (matching npm 0.2.x): only access-granting material
  is redacted by key; `Cookie`/`Authorization` never logged (allowlist); business
  data logged verbatim. Loud env-var validation. Targets `net8.0` (runs on 8/9/10).
  Published to GitHub Packages (and nuget.org when a key is configured).

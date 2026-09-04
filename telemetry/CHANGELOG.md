# Changelog

All notable changes to `@insidebeehive/telemetry`. Format based on
[Keep a Changelog](https://keepachangelog.com/). Versioning is
[SemVer](https://semver.org/); while `0.x`, a **minor** bump may change
default behavior and a **patch** is a fix or additive change.

## [0.3.1] - 2026-09-04

### Fixed
- **The app logger now owns `timestamp` — a caller-supplied `timestamp` in log
  meta can no longer redefine the line's time.** winston's `timestamp()` format
  only fills the field when absent, so `logger.info(msg, { timestamp })` used to
  send the caller's value straight to stdout. When that value was not a wall
  clock — e.g. an 18-digit `YYYYMMDDHHmmssSSSS` signature stamp — VictoriaLogs
  (`_time_field: timestamp`) read it as a **nanosecond epoch** (`202609040407160286`
  → `1976-06-03`), which fell below the retention floor, so the line was
  **silently dropped**. The logger now always stamps its own ISO time and
  preserves any caller value under `timestamp_field` (nothing is lost).
- Same protection extended to the other logger-owned fields: `logger` (the
  VictoriaLogs stream discriminator), `service`, and `runtime` can no longer be
  clobbered by per-call meta; a colliding value is kept under `<name>_field`.

## [0.3.0] - 2026-09-03

### Added
- Form bodies (`application/x-www-form-urlencoded`) are now **decoded** into a
  key/value object and rendered like JSON bodies — `user=amit+kumar&note=hi%20there`
  logs as `{"user":"amit kumar","note":"hi there"}` (no `%XX`/`+` escapes),
  per-key redacted, repeated keys collapse to an array, honors `HTTP_LOG_BODY_MODE`.

### Changed
- **Static assets are skipped by default.** New `HTTP_LOG_IGNORE_EXTENSIONS`
  (default `js,mjs,cjs,css,map,ico,png,jpg,jpeg,gif,svg,webp,avif,woff,woff2,ttf,eot`)
  drops any request whose path ends in a front-end static extension — no line is
  emitted. Business downloads (`pdf`,`csv`,`xlsx`,`zip`) are deliberately **not**
  in the default. Set `HTTP_LOG_IGNORE_EXTENSIONS=off` (or a custom list) to change
  it. Behavior change: asset requests that were logged before are now skipped unless
  you override.

## [0.2.2] - 2026-09-03

### Added
- `runtime` field (`node` | `bun`) on every `http.access` and app line, for
  fleet-wide grouping and to distinguish the HTTP capture path. Also added to the
  collector's VictoriaLogs `_stream_fields` (a bhgrafana deploy activates the
  stream-field side).

## [0.2.1] - 2026-09-02

### Added
- `import logger from "@insidebeehive/telemetry/logger"` — a default-export subpath
  resolving to the same lazy singleton as the named `{ logger }` export (both remain
  supported; CJS, ESM, and Bun verified).

## [0.2.0] - 2026-09-02

### Changed
- **Redaction policy reset — "logs are evidence."** Only access-granting material is
  redacted now: passwords, OTPs/PINs, session ids & tokens, API keys, secrets,
  webhook signatures. `Cookie`/`Authorization` remain structurally never-logged
  (header allowlist). **Business data — card/account numbers, CVV, amounts, ids,
  bank/UPI refs, URL paths — is now logged VERBATIM.** This is breaking for anyone
  who relied on card-number scrubbing.

### Removed
- PCI-style Luhn card-number scrubbing (`[REDACTED-PAN]` / `[CUT-DIGITS]`) that
  shipped in 0.1.7–0.1.11. Its false positives redacted the very numeric refs these
  logs exist to keep. If you need PAN-free logs, pin `0.1.11` or keep card-bearing
  payloads out of payload logging.

## [0.1.11] - 2026-09-02

### Fixed
- Card-number scrubbing extended to URL **paths** and allowlisted **header** values,
  and to exported span URL attributes. *(Superseded by the 0.2.0 policy reset.)*

## [0.1.10] - 2026-09-02

### Fixed
- Card-number scrub now catches a PAN glued to adjacent digits under a non-sensitive
  key; truncation-boundary hardening. *(Superseded by the 0.2.0 policy reset.)*

## [0.1.9] - 2026-09-02

### Fixed
- A card number sliced by the body cap no longer leaks a recoverable prefix (masked
  as `[CUT-DIGITS]`). *(PAN handling superseded by 0.2.0.)*
- Bodies in a declared non-ASCII charset (e.g. `utf-16le`) now log a size placeholder
  instead of unscrubbed bytes; NUL bytes stripped before scrubbing.
- `--import @insidebeehive/telemetry/register.mjs` (and `/register.js`) explicit-file
  subpaths now resolve.
- Compressed/multipart **request** bodies now log a `[gzip N bytes]` /
  `[multipart/form-data N bytes]` placeholder (matching the response side).

## [0.1.8] - 2026-09-02

### Fixed
- Credential-key matching corrected for underscored keys (`card_no`, `account_no`).
  *(PAN-specific parts superseded by 0.2.0.)*
- Env values and `content-type` matching are now case-insensitive (`HTTP_LOG=OFF`,
  `APPLICATION/JSON` work).
- Loud validation for `HTTP_LOG_BODY_MODE`, `OTEL_TRACES_SAMPLER_ARG` (incl. empty)
  and `LOGTAIL_URL` — invalid values warn and fall back instead of silently
  disabling capture.

## [0.1.7] - 2026-09-02

### Fixed
- **Bodies over the size cap, and malformed bodies, are never logged raw** — a
  best-effort key/value scrub is the backstop, so a credential sliced by the cap is
  still redacted by key.
- `application/x-www-form-urlencoded` bodies get per-key redaction.
- Logtail transport is bounded on outage (in-flight cap + timeout) — no more
  unbounded socket/memory growth against a hanging endpoint.
- Deeply nested `Error`s serialize with message + stack (were `{}`).
- Logged header values capped at 512 chars.

## [0.1.0] – [0.1.6] - 2026-09-01

### Added
- Initial releases. Zero-code activation via
  `NODE_OPTIONS="--import @insidebeehive/telemetry/register"` (or
  `require(...).init()`). OpenTelemetry traces with parent-based sampling, OTLP
  export gated on `OTEL_EXPORTER_OTLP_ENDPOINT`, and a scrubbing span processor.
  100% HTTP access + payload logging as one JSON line per request (`logger=http`),
  payload on by default, bodies as a JSON-encoded string by default
  (`HTTP_LOG_BODY_MODE=object` for nested). A winston app logger (`logger=app`) with
  an `audit` level no `LOG_LEVEL` can silence. Fly.io resource detection with a
  copy-paste warning when platform env is missing. Node CJS/ESM and Bun. Optional
  Logtail/BetterStack sink for app logs. Published to npmjs and GitHub Packages.

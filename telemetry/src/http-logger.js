"use strict";
/**
 * HTTP access + payload logging with zero app code — the 100% "which request
 * went where and responded how" record. Emits exactly ONE JSON line per
 * request straight to stdout (Fly log stream -> Vector -> VictoriaLogs):
 *
 *   http.access (level=info) — method, path/route, url, host, status,
 *   duration, ip, trace_id. Always, for every request. When the
 *   HTTP_LOG_PAYLOAD policy fires (default: errors + slow), the SAME line
 *   additionally carries redacted headers and capped bodies, making a failed
 *   transaction's record self-contained. Enriched lines: `req_body:*`.
 *
 * Every line carries logger=http — the field vector.yaml declares as a
 * VictoriaLogs stream field, which is what keeps this firehose queryable
 * separately from ordinary app logs.
 *
 * Capture mechanism: diagnostics_channel `http.server.request.start` — the
 * officially supported core API for exactly this (Node >= 22; the package's
 * engines field enforces it). No module patching, so it works the same for
 * CJS (NestJS), ESM (Remix/Vite) and even bundled server builds. Body capture
 * taps the streams observe-only: request `data` listeners broadcast, and
 * response write/end are wrapped per-instance.
 *
 * Deliberately independent of the OTel SDK: if tracing is disabled these logs
 * still flow (trace ids then come from the caller's traceparent header, when
 * present). Lines bypass the app's logger entirely — no winston coupling.
 *
 * Env knobs (per-app policy lives in fly.toml, no redeploys of code):
 *   HTTP_LOG=on                  master switch, default ON everywhere.
 *                                Set HTTP_LOG=off where the lines are
 *                                unwanted (local dev, test runs).
 *   HTTP_LOG_PAYLOAD=always      always|errors|off (default always: every
 *                                line carries redacted headers + capped
 *                                bodies, so any request's record is
 *                                self-contained evidence)
 *   HTTP_LOG_SLOW_MS=1000        "errors" tier also fires above this duration
 *   HTTP_LOG_BODY_MAX=4096       bytes kept per body
 *   HTTP_LOG_BODY_MODE=string    string | object. string (default): bodies
 *                                are redacted then kept as ONE JSON-encoded
 *                                string field — lean field panel per line;
 *                                query via `| unpack_json from req_body`.
 *                                object: parsed+redacted bodies land as
 *                                nested fields (req_body.amount queryable
 *                                directly; VictoriaLogs flattens dicts with
 *                                dots and stringifies arrays at ingest) —
 *                                per-app opt-in for stable, hot-queried
 *                                schemas. Truncated/non-JSON/compressed
 *                                bodies are strings/placeholders either way.
 *   HTTP_LOG_PAYLOAD_ROUTES=     comma path-prefixes that always get payloads
 *   HTTP_LOG_IGNORE_PATHS=       exact paths (or "prefix/") to skip entirely
 *                                (default /,/health,/healthz,/favicon.ico)
 */

const INSTALLED = Symbol.for("beehive.telemetry.httpLogger");

function install() {
  if (global[INSTALLED]) return;
  global[INSTALLED] = true;

  const env = (name, dflt) => {
    const v = process.env[name];
    return v === undefined || v === "" ? dflt : v;
  };

  if (env("HTTP_LOG", "on").toLowerCase() === "off") {
    console.log("[telemetry] http logger disabled via HTTP_LOG=off");
    return;
  }

  const { redactSensitive, redactUrl, pickHeaders, scrubText, REDACTED } = require("./redact");
  const { resolveServiceName } = require("./service-name");

  // Invalid env values fall back LOUDLY to safe defaults — a typo must never
  // silently disable evidence capture (found by external QA).
  let PAYLOAD_MODE = env("HTTP_LOG_PAYLOAD", "always").toLowerCase(); // always | errors | off
  if (!["always", "errors", "off"].includes(PAYLOAD_MODE)) {
    console.warn(`[telemetry] invalid HTTP_LOG_PAYLOAD "${PAYLOAD_MODE}" — using "always" (valid: always|errors|off)`);
    PAYLOAD_MODE = "always";
  }
  let SLOW_MS = Number(env("HTTP_LOG_SLOW_MS", "1000"));
  if (!Number.isFinite(SLOW_MS) || SLOW_MS < 0) {
    console.warn(`[telemetry] invalid HTTP_LOG_SLOW_MS "${process.env.HTTP_LOG_SLOW_MS}" — using 1000`);
    SLOW_MS = 1000;
  }
  let BODY_MAX = Number(env("HTTP_LOG_BODY_MAX", "4096"));
  if (!Number.isFinite(BODY_MAX) || BODY_MAX <= 0) {
    console.warn(`[telemetry] invalid HTTP_LOG_BODY_MAX "${process.env.HTTP_LOG_BODY_MAX}" — using 4096`);
    BODY_MAX = 4096;
  }
  let BODY_MODE = env("HTTP_LOG_BODY_MODE", "string").toLowerCase(); // string | object
  if (!["string", "object"].includes(BODY_MODE)) {
    console.warn(`[telemetry] invalid HTTP_LOG_BODY_MODE "${process.env.HTTP_LOG_BODY_MODE}" — using "string" (valid: string|object)`);
    BODY_MODE = "string";
  }
  const PAYLOAD_ROUTES = env("HTTP_LOG_PAYLOAD_ROUTES", "").split(",").map((s) => s.trim()).filter(Boolean);
  const IGNORE = env("HTTP_LOG_IGNORE_PATHS", "/,/health,/healthz,/favicon.ico").split(",").map((s) => s.trim()).filter(Boolean);
  const SERVICE = resolveServiceName();

  // Same semantics as tracing.js: exact match unless the entry ends in "/"
  // (subtree prefix); bare "/" stays exact or it would match everything.
  const isIgnored = (path) =>
    IGNORE.some((ignore) => {
      if (ignore.endsWith("/") && ignore !== "/") return path.startsWith(ignore);
      return path === ignore;
    });

  // Optional dependency: when the OTel SDK is running, the ACTIVE span is the
  // richest source of ids; when it is not (or before it), the caller's
  // traceparent header still stitches the hops together.
  let otel = null;
  try {
    otel = require("@opentelemetry/api");
  } catch {
    /* tracing genuinely absent — header fallback only */
  }

  // trace_sampled says whether this trace_id will resolve to stored spans in
  // the traces UI (head sampling keeps ~10%) — saves chasing ids that were
  // never exported. Convention carried over from the legacy core logger.
  const activeIds = () => {
    try {
      const span = otel && otel.trace.getSpan(otel.context.active());
      if (span) {
        const c = span.spanContext();
        if (c && c.traceId && c.traceId !== "00000000000000000000000000000000") {
          return { trace_id: c.traceId, span_id: c.spanId, trace_sampled: (c.traceFlags & 1) === 1 };
        }
      }
    } catch {
      /* never let id capture break a request */
    }
    return null;
  };

  const headerIds = (req) => {
    const tp = req.headers && req.headers.traceparent;
    if (typeof tp === "string") {
      const m = /^[0-9a-f]{2}-([0-9a-f]{32})-([0-9a-f]{16})-([0-9a-f]{2})/.exec(tp);
      if (m) return { trace_id: m[1], span_id: m[2], trace_sampled: (parseInt(m[3], 16) & 1) === 1 };
    }
    return {};
  };

  const emit = (record) => {
    try {
      process.stdout.write(JSON.stringify(record) + "\n");
    } catch {
      /* a logging failure must never touch the request */
    }
  };

  /**
   * Bodies become log text only when they are safely textual:
   *   - compressed bytes are NEVER decoded (a capped prefix of a gzip stream
   *     cannot be decompressed anyway) — a size placeholder is logged instead;
   *   - responses must be JSON (jsonOnly) — this is what keeps Remix streamed
   *     HTML documents out of the payload lines; requests may also be
   *     text/* or urlencoded, as before;
   *   - JSON is parsed and field-redacted before logging.
   */
  // The regex scrubbers can only see through ASCII-compatible bytes. A body
  // in any other DECLARED charset (utf-16/32, ...) gets a size placeholder
  // like compressed bytes do — utf-16le JSON otherwise sails past every
  // scrub NUL-by-NUL (external QA round 3).
  const ASCII_COMPATIBLE_CHARSETS = new Set(["utf-8", "utf8", "us-ascii", "ascii", "iso-8859-1", "iso8859-1", "latin1", "latin-1", "windows-1252"]);

  const renderBody = (chunks, total, contentType, contentEncoding, jsonOnly) => {
    if (total === 0) return undefined;
    const enc = String(contentEncoding || "").toLowerCase();
    if (enc && enc !== "identity") return `[${enc} ${total} bytes]`;
    const ct = String(contentType || "").toLowerCase();
    const charset = /charset\s*=\s*"?([a-z0-9_\-]+)"?/.exec(ct);
    if (charset && !ASCII_COMPATIBLE_CHARSETS.has(charset[1])) return `[${ct.split(";")[0] || "text"} ${charset[1]} ${total} bytes]`;
    const textual = jsonOnly
      ? ct.includes("json")
      : ct.includes("json") || ct.startsWith("text/") || ct.includes("urlencoded") || ct === "";
    if (!textual) return `[${ct.split(";")[0] || "binary"} ${total} bytes]`;
    const captured = Buffer.concat(chunks);
    // NUL bytes are never legitimate in textual bodies — strip them so
    // NUL-interleaved digits/keys (utf-16 bytes behind a LYING utf-8
    // charset) cannot slip past the scrubbers below.
    const text = captured.toString("utf8").replace(/\u0000/g, "");
    if (ct.includes("urlencoded")) {
      // Login/payment form encoding — per-key redaction like query strings
      // (found leaking verbatim by external QA).
      try {
        const params = new URLSearchParams(text);
        for (const key of Array.from(params.keys())) {
          if (require("./redact").isSensitiveKey(key)) params.set(key, REDACTED);
        }
        return params.toString().replace(/%5BREDACTED%5D/g, REDACTED);
      } catch {
        return scrubText(text);
      }
    }
    if (ct.includes("json") || ct === "") {
      try {
        const parsed = redactSensitive(JSON.parse(text));
        return BODY_MODE === "string" ? JSON.stringify(parsed) : parsed;
      } catch {
        /* truncated or malformed JSON — NEVER return it raw: best-effort
           key/value + PAN scrub instead (external QA found the raw
           fallback leaking credentials on >cap and malformed bodies). */
        return scrubText(text);
      }
    }
    return scrubText(text);
  };

  const BODYLESS = new Set(["GET", "HEAD", "OPTIONS", "DELETE"]);

  // Request bodies deliberately never read (compressed, multipart, other
  // binary) still get size evidence on enriched lines. content-length is the
  // only safe source — the stream was never tapped (external QA round 3
  // found these lines carried no req_body at all, unlike the response side).
  const bodyPlaceholder = (req) => {
    const headers = req.headers || {};
    if (BODYLESS.has(req.method)) return undefined;
    const enc = String(headers["content-encoding"] || "").toLowerCase();
    const ct = String(headers["content-type"] || "").split(";")[0].toLowerCase().trim();
    const clRaw = String(headers["content-length"] || "");
    const size = /^\d{1,15}$/.test(clRaw) ? `${clRaw} bytes` : "unknown size";
    if (enc && enc !== "identity") return `[${enc} ${size}]`;
    if (ct && !(ct.includes("json") || ct.startsWith("text/") || ct.includes("urlencoded"))) return `[${ct} ${size}]`;
    return undefined;
  };

  const seen = new WeakSet();

  function onRequest(req, res) {
    try {
      if (!req || !res || seen.has(req)) return;
      seen.add(req);
      if (req.headers && req.headers.upgrade) return; // websockets are not request/response

      const path = String(req.url || "").split("?")[0];
      if (isIgnored(path)) return;

      const startNs = process.hrtime.bigint();
      const wantPayload = PAYLOAD_MODE !== "off";
      const state = {
        ids: null,
        reqChunks: [],
        reqBytes: 0,
        resChunks: [],
        resBytes: 0,
        done: false,
      };

      if (wantPayload && !BODYLESS.has(req.method)) {
        const ct = String(req.headers["content-type"] || "").toLowerCase();
        const reqEncoded = req.headers["content-encoding"] && String(req.headers["content-encoding"]).toLowerCase() !== "identity";
        if (!reqEncoded && (ct.includes("json") || ct.startsWith("text/") || ct.includes("urlencoded"))) {
          // Observe-only: 'data' listeners broadcast, so body parsers attached
          // by the framework (same macrotask, before the first async data
          // event) still receive every chunk. If nothing else ever reads the
          // body this listener drains it, which is harmless post-response.
          req.on("data", (chunk) => {
            try {
              const len = typeof chunk === "string" ? Buffer.byteLength(chunk) : chunk.length;
              if (state.reqBytes < BODY_MAX) state.reqChunks.push(Buffer.from(chunk).subarray(0, BODY_MAX - state.reqBytes));
              state.reqBytes += len;
            } catch {
              /* observe-only — a capture failure must never touch the request */
            }
          });
        }
      }

      // Wrap write/end per-INSTANCE (not prototype): copies response bytes up
      // to the cap, and — the subtle part — captures trace ids here, because
      // these calls run inside the app's async context where the OTel span is
      // active. The diagnostics_channel callback itself runs before the span
      // exists, so ids cannot be taken there.
      const capture = (chunk) => {
        if (!state.ids) state.ids = activeIds();
        if (chunk == null) return;
        if (typeof chunk !== "string" && !Buffer.isBuffer(chunk)) return;
        const len = typeof chunk === "string" ? Buffer.byteLength(chunk) : chunk.length;
        if (wantPayload && state.resBytes < BODY_MAX) state.resChunks.push(Buffer.from(chunk).subarray(0, BODY_MAX - state.resBytes));
        state.resBytes += len;
      };
      const origWrite = res.write;
      const origEnd = res.end;
      res.write = function (chunk) {
        try {
          capture(chunk);
        } catch {
          /* observe-only */
        }
        return origWrite.apply(this, arguments);
      };
      res.end = function (chunk) {
        try {
          capture(chunk);
        } catch {
          /* observe-only */
        }
        return origEnd.apply(this, arguments);
      };

      // 'close' fires after 'finish' on success AND on aborted/errored
      // requests — one completion path for both.
      res.on("close", () => {
        try {
          if (state.done) return;
          state.done = true;

          const durationMs = Number(process.hrtime.bigint() - startNs) / 1e6;
          const finished = res.writableEnded === true;
          const status = finished ? res.statusCode : 499; // nginx convention: client closed request
          const ids = state.ids || activeIds() || headerIds(req);

          // Express/Nest populate req.route by close-time; the template keeps
          // path cardinality sane in queries. Remix has no visible route here
          // (its adapter mounts a catch-all), so the raw redacted path is the
          // identifier — an accepted limitation of transport-level capture.
          const route = req.route && req.route.path ? `${req.baseUrl || ""}${req.route.path}` : undefined;

          const base = {
            ts: new Date().toISOString(),
            logger: "http",
            service: SERVICE,
            method: req.method,
            path,
            route,
            url: redactUrl(req.originalUrl || req.url || ""),
            // Host HEADER — named http_host because plain `host` would collide
            // with the Fly envelope's machine-host STREAM field (verified in
            // the smoke test: it re-keyed the http stream by Host header,
            // which is client-controlled — unbounded stream cardinality).
            http_host: (req.headers && req.headers.host) || undefined,
            status,
            duration_ms: Math.round(durationMs * 10) / 10,
            ip: (String(req.headers["x-forwarded-for"] || "").split(",")[0] || (req.socket && req.socket.remoteAddress) || "").trim() || undefined,
            aborted: finished ? undefined : true,
            ...ids,
          };

          const record = { level: "info", message: "http.access", ...base, res_bytes: state.resBytes || undefined };

          const wantThisPayload =
            PAYLOAD_MODE === "always" ||
            (PAYLOAD_MODE === "errors" && (status >= 400 || durationMs >= SLOW_MS)) ||
            PAYLOAD_ROUTES.some((prefix) => path.startsWith(prefix));

          // ONE line per request, always. When the payload policy fires, the
          // SAME line carries headers and bodies, so the record of a failed
          // transaction is self-contained evidence — and "all requests"
          // queries never deal with a second message type or double-count.
          // Select enriched lines with `req_body:*` (or `res_body:*`).
          if (wantPayload && wantThisPayload) {
            let resHeaders = {};
            try {
              resHeaders = pickHeaders(res.getHeaders ? res.getHeaders() : {}, ["content-type", "content-length", "content-encoding"]);
            } catch {
              /* headers already sent weirdly — skip them */
            }
            record.payload = true; // stable selector for enriched lines in either body mode
            record.req_headers = pickHeaders(req.headers);
            record.req_body = renderBody(state.reqChunks, state.reqBytes, req.headers["content-type"], req.headers["content-encoding"], false);
            if (record.req_body === undefined) record.req_body = bodyPlaceholder(req);
            record.req_body_truncated = state.reqBytes > BODY_MAX || undefined;
            record.res_headers = resHeaders;
            record.res_body = renderBody(state.resChunks, state.resBytes, resHeaders["content-type"], resHeaders["content-encoding"], true);
            record.res_body_truncated = state.resBytes > BODY_MAX || undefined;
          }

          emit(record);
        } catch (error) {
          warnOnce(error);
        }
      });
    } catch (error) {
      warnOnce(error);
    }
  }

  let warned = false;
  function warnOnce(error) {
    if (warned) return;
    warned = true;
    console.error("[telemetry] http logger error (reported once)", error);
  }

  if (process.versions && process.versions.bun) {
    // Bun implements node:http but does NOT publish the diagnostics_channel
    // request events (verified empirically on bun 1.4). Its Server is still
    // an EventEmitter, so wrap emit("request") — the classic APM mechanism.
    const wrapEmit = (proto) => {
      const orig = proto.emit;
      proto.emit = function (event) {
        if (event === "request") {
          try {
            onRequest(arguments[1], arguments[2]);
          } catch (error) {
            warnOnce(error);
          }
        }
        return orig.apply(this, arguments);
      };
    };
    wrapEmit(require("node:http").Server.prototype);
    try {
      wrapEmit(require("node:https").Server.prototype);
    } catch {
      /* https server wrap is best-effort on Bun */
    }
    console.log(`[telemetry] http logger enabled (bun server-wrap, payload=${PAYLOAD_MODE})`);
  } else {
    // Official core API (Node >= 22): no patching anywhere. The Node 20
    // Server.prototype.emit fallback the old preload carried is gone — the
    // fleet images pin Node 24.
    const dc = require("node:diagnostics_channel");
    dc.subscribe("http.server.request.start", (message) => {
      if (message) onRequest(message.request, message.response);
    });
    console.log(`[telemetry] http logger enabled (diagnostics_channel, payload=${PAYLOAD_MODE})`);
  }
}

module.exports = { install };

"use strict";
/**
 * HTTP access + payload logging with zero app code — the 100% "which request
 * went where and responded how" record. Emits one or two JSON lines per
 * request straight to stdout (Fly log stream -> Vector -> VictoriaLogs):
 *
 *   http.access  (level=info)  — method, route, status, duration, trace_id.
 *                                Always, for every request.
 *   http.payload (level=debug) — redacted headers + capped bodies.
 *                                Per HTTP_LOG_PAYLOAD policy (default: errors).
 *
 * Capture mechanism, in order of preference:
 *   - Node >= 22: diagnostics_channel `http.server.request.start` — the
 *     officially supported core API for exactly this; no patching at all.
 *   - Node 20:   wrap http(s).Server.prototype.emit — the same mechanism
 *     every APM agent (dd-trace, New Relic, Elastic, OTel itself) ships.
 * Body capture on either path taps the streams observe-only: request `data`
 * listeners broadcast, and response write/end are wrapped per-instance.
 *
 * Deliberately independent of the OTel SDK: if tracing is disabled these logs
 * still flow (trace ids then come from the caller's traceparent header, when
 * present). Lines bypass the app's logger entirely — no winston coupling.
 *
 * Env knobs (flydeck-owned, no redeploys of app code to change policy):
 *   HTTP_LOG=off                  master switch (default on)
 *   HTTP_LOG_PAYLOAD=errors      errors|always|off (default errors)
 *   HTTP_LOG_SLOW_MS=1000        "errors" tier also fires above this duration
 *   HTTP_LOG_BODY_MAX=4096       bytes kept per body
 *   HTTP_LOG_PAYLOAD_ROUTES=     comma path-prefixes that always get payloads
 *   HTTP_LOG_IGNORE_PATHS=       exact paths (or "prefix/") to skip entirely
 *                                (default /,/health,/healthz,/favicon.ico)
 *   HTTP_LOG_MODE=auto           auto|channels|wrap
 */

const INSTALLED = Symbol.for("beehive.telemetry.httpLogger");
if (global[INSTALLED]) {
  module.exports = {};
} else {
  global[INSTALLED] = true;
  install();
}

function install() {
  const env = (name, dflt) => {
    const v = process.env[name];
    return v === undefined || v === "" ? dflt : v;
  };

  if (env("HTTP_LOG", "on") === "off") {
    console.log("[telemetry-preload] http logger disabled via HTTP_LOG=off");
    return;
  }

  const { redactSensitive, redactUrl, pickHeaders } = require("./redact");

  const PAYLOAD_MODE = env("HTTP_LOG_PAYLOAD", "errors"); // errors | always | off
  const SLOW_MS = Number(env("HTTP_LOG_SLOW_MS", "1000"));
  const BODY_MAX = Number(env("HTTP_LOG_BODY_MAX", "4096"));
  const PAYLOAD_ROUTES = env("HTTP_LOG_PAYLOAD_ROUTES", "").split(",").map((s) => s.trim()).filter(Boolean);
  const IGNORE = env("HTTP_LOG_IGNORE_PATHS", "/,/health,/healthz,/favicon.ico").split(",").map((s) => s.trim()).filter(Boolean);
  const MODE = env("HTTP_LOG_MODE", "auto"); // auto | channels | wrap
  const SERVICE = process.env.OTEL_SERVICE_NAME || process.env.FLY_APP_NAME || "unknown-service";

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

  const activeIds = () => {
    try {
      const span = otel && otel.trace.getSpan(otel.context.active());
      if (span) {
        const c = span.spanContext();
        if (c && c.traceId && c.traceId !== "00000000000000000000000000000000") {
          return { trace_id: c.traceId, span_id: c.spanId };
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
      const m = /^[0-9a-f]{2}-([0-9a-f]{32})-([0-9a-f]{16})-/.exec(tp);
      if (m) return { trace_id: m[1], span_id: m[2] };
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

  const renderBody = (chunks, total, contentType) => {
    if (total === 0) return undefined;
    const ct = String(contentType || "");
    const textual = ct.includes("json") || ct.startsWith("text/") || ct.includes("urlencoded") || ct === "";
    if (!textual) return `[${ct.split(";")[0] || "binary"} ${total} bytes]`;
    let text = Buffer.concat(chunks).toString("utf8");
    if (ct.includes("json") || ct === "") {
      try {
        return JSON.stringify(redactSensitive(JSON.parse(text)));
      } catch {
        /* not JSON after all — fall through to capped raw text */
      }
    }
    return text;
  };

  const BODYLESS = new Set(["GET", "HEAD", "OPTIONS", "DELETE"]);
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
        const ct = String(req.headers["content-type"] || "");
        if (ct.includes("json") || ct.startsWith("text/") || ct.includes("urlencoded")) {
          // Observe-only: 'data' listeners broadcast, so body parsers attached
          // by the framework (same macrotask, before the first async data
          // event) still receive every chunk. If nothing else ever reads the
          // body this listener drains it, which is harmless post-response.
          req.on("data", (chunk) => {
            const len = typeof chunk === "string" ? Buffer.byteLength(chunk) : chunk.length;
            if (state.reqBytes < BODY_MAX) state.reqChunks.push(Buffer.from(chunk).subarray(0, BODY_MAX - state.reqBytes));
            state.reqBytes += len;
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

          const base = {
            ts: new Date().toISOString(),
            logger: "http",
            service: SERVICE,
            method: req.method,
            path,
            url: redactUrl(req.originalUrl || req.url || ""),
            status,
            duration_ms: Math.round(durationMs * 10) / 10,
            ip: (String(req.headers["x-forwarded-for"] || "").split(",")[0] || (req.socket && req.socket.remoteAddress) || "").trim() || undefined,
            aborted: finished ? undefined : true,
            ...ids,
          };

          emit({ level: "info", message: "http.access", ...base, res_bytes: state.resBytes || undefined });

          const wantThisPayload =
            PAYLOAD_MODE === "always" ||
            (PAYLOAD_MODE === "errors" && (status >= 400 || durationMs >= SLOW_MS)) ||
            PAYLOAD_ROUTES.some((prefix) => path.startsWith(prefix));

          if (wantPayload && wantThisPayload) {
            let resHeaders = {};
            try {
              resHeaders = pickHeaders(res.getHeaders ? res.getHeaders() : {}, ["content-type", "content-length"]);
            } catch {
              /* headers already sent weirdly — skip them */
            }
            emit({
              level: "debug",
              message: "http.payload",
              ...base,
              req_headers: pickHeaders(req.headers),
              req_body: renderBody(state.reqChunks, state.reqBytes, req.headers["content-type"]),
              req_body_truncated: state.reqBytes > BODY_MAX || undefined,
              res_headers: resHeaders,
              res_body: renderBody(state.resChunks, state.resBytes, resHeaders["content-type"]),
              res_body_truncated: state.resBytes > BODY_MAX || undefined,
            });
          }
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
    console.error("[telemetry-preload] http logger error (reported once)", error);
  }

  const nodeMajor = Number(String(process.versions.node).split(".")[0]);
  const useChannels = MODE === "channels" || (MODE === "auto" && nodeMajor >= 22);

  if (useChannels) {
    // Official core API (Node >= 22): no patching anywhere.
    const dc = require("node:diagnostics_channel");
    dc.subscribe("http.server.request.start", (message) => {
      if (message) onRequest(message.request, message.response);
    });
    console.log(`[telemetry-preload] http logger enabled (diagnostics_channel, payload=${PAYLOAD_MODE})`);
  } else {
    // Node 20 fallback: the standard APM-agent mechanism. Removed the day the
    // fleet standardises on Node >= 22.
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
    wrapEmit(require("http").Server.prototype);
    wrapEmit(require("https").Server.prototype);
    console.log(`[telemetry-preload] http logger enabled (server wrap, payload=${PAYLOAD_MODE})`);
  }
}

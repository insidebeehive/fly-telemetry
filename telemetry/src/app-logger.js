"use strict";
/**
 * The app logger — `logger` from the package root. Zero config:
 *
 *   const { logger } = require("@insidebeehive/telemetry");
 *   logger.info("order placed", { orderId, amount });
 *   logger.audit("order.settled", { actor, userId, amount });
 *
 * `audit` is a custom LEVEL (priority 0, above error), not a stream: audit
 * lines stay in logger=app and are selected with `level:audit`. Priority 0
 * means LOG_LEVEL can never silence them — an app quieted to
 * LOG_LEVEL=error still records every audit event.
 *
 * - JSON lines to stdout on Fly / in production (VictoriaLogs indexes the
 *   fields); pretty-printed with colors locally.
 * - Every line carries logger=app via winston defaultMeta — the stream-level
 *   partner of the http logger's logger=http, so Grafana separates app logs
 *   from the HTTP firehose at the VictoriaLogs STREAM level. Convention:
 *   http | app | (absent).
 * - service is stamped with the same resolution used for spans + http lines.
 * - trace_id/span_id appear automatically on lines logged inside a request
 *   (the OTel winston instrumentation injects them) when telemetry is active.
 * - LOG_LEVEL env sets the level (default info) — it gates ONLY this app
 *   logger; the http logger writes to stdout directly and ignores it.
 * - logger.child({ module: "wallet" }) works as normal winston.
 * - Errors: logger.error(err) or logger.error("context", { err, ...fields })
 *   both capture the stack (nested Errors are serialised by a format step).
 * - Uncaught exceptions / unhandled rejections are logged as one structured
 *   line, then the process exits (Fly restarts it).
 *
 * Built lazily on FIRST USE so winston is required after the OTel
 * instrumentation registers its require hook — required earlier, the patch
 * would silently miss and trace injection wouldn't apply. (With NODE_OPTIONS
 * activation the ordering is always safe; the laziness protects the
 * programmatic-init() path, and means pino-only apps never load winston.)
 */

const { resolveServiceName } = require("./service-name");

let realLogger = null;

/**
 * Optional Logtail (BetterStack) shipping for APP logs only — active when
 * BOTH LOGTAIL_URL and LOGTAIL_TOKEN (alias LOGTAIL_SOURCE_TOKEN) are set.
 * http.access lines never pass through winston, so they are excluded by
 * construction. Fail-safe by design: batched, capped, fire-and-forget —
 * a Logtail outage drops the copy, never blocks a request or crashes the
 * app; stdout (-> the log pipeline) remains the source of truth. Crash
 * lines (uncaught exceptions) go to stdout only — a fetch mid-crash is
 * not a promise worth making.
 */
function makeLogtailTransport() {
  const url = process.env.LOGTAIL_URL;
  const token = process.env.LOGTAIL_TOKEN || process.env.LOGTAIL_SOURCE_TOKEN;
  if (!url || !token || typeof fetch !== "function") return null;
  try {
    new URL(url);
  } catch {
    console.warn(`[telemetry] invalid LOGTAIL_URL "${url}" — Logtail shipping disabled`);
    return null;
  }

  const Transport = require("winston-transport");
  const MAX_BATCH = 100;    // flush at this many buffered lines...
  const MAX_BUFFER = 1000;  // ...drop beyond this many (Logtail down)
  const FLUSH_MS = 2000;

  const FETCH_TIMEOUT_MS = 5000; // a hanging endpoint must not pin sockets
  const MAX_INFLIGHT = 2; //        ...nor accumulate unbounded requests

  class LogtailTransport extends Transport {
    constructor() {
      super({});
      this.buffer = [];
      this.inflight = 0;
      this.timer = setInterval(() => this.flush(), FLUSH_MS);
      if (this.timer.unref) this.timer.unref();
    }

    log(info, callback) {
      try {
        const event = { dt: new Date().toISOString() };
        for (const key of Object.keys(info)) event[key] = info[key];
        if (this.buffer.length < MAX_BUFFER) this.buffer.push(event);
        if (this.buffer.length >= MAX_BATCH) this.flush();
      } catch {
        /* shipping is best-effort, always */
      }
      callback();
    }

    flush() {
      // With the endpoint hanging, in-flight requests are capped and each is
      // aborted after FETCH_TIMEOUT_MS; the buffer itself is capped at
      // MAX_BUFFER — so sockets and memory stay bounded no matter what the
      // far end does (external QA found unbounded fd/RSS growth here).
      if (!this.buffer.length || this.inflight >= MAX_INFLIGHT) return;
      const batch = this.buffer.splice(0, 500); // bounded POST size
      this.inflight += 1;
      const done = () => {
        this.inflight -= 1;
      };
      try {
        fetch(url, {
          method: "POST",
          headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
          body: JSON.stringify(batch),
          signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
        }).then(done, done);
      } catch {
        done();
        /* drop the copy; stdout already has these lines */
      }
    }
  }

  return new LogtailTransport();
}

// Standard npm levels plus `audit` at the HIGHEST priority (0): winston
// logs a line when its level number <= the logger's level number, so audit
// passes every LOG_LEVEL, error included. Level, not stream, by decision —
// audit lines live in logger=app and are queried with `level:audit`.
const LEVELS = { audit: 0, error: 1, warn: 2, info: 3, http: 4, verbose: 5, debug: 6, silly: 7 };

function buildLogger() {
  const winston = require("winston");
  winston.addColors({ ...winston.config.npm.colors, audit: "magenta" });
  const pretty = !process.env.FLY_APP_NAME && process.env.NODE_ENV !== "production";

  // Errors nested in meta — logger.error("payment failed", { err, orderId })
  // — would JSON.stringify to {} (Error props are non-enumerable). Serialise
  // them so the stack always lands in the line. Top-level Errors
  // (logger.error(err)) are handled by winston's errors({stack}) format.
  const serializeDeep = (value, depth, seen) => {
    if (value instanceof Error) {
      return {
        message: value.message,
        stack: value.stack,
        ...(value.code !== undefined ? { code: value.code } : {}),
      };
    }
    if (!value || typeof value !== "object" || depth >= 4 || seen.has(value)) return value;
    seen.add(value);
    if (Array.isArray(value)) return value.map((item) => serializeDeep(item, depth + 1, seen));
    const out = {};
    for (const key of Object.keys(value)) out[key] = serializeDeep(value[key], depth + 1, seen);
    return out;
  };
  const serializeErrors = winston.format((info) => {
    for (const key of Object.keys(info)) {
      info[key] = serializeDeep(info[key], 0, new WeakSet());
    }
    return info;
  });

  // Fields the logger OWNS. `logger` is the VictoriaLogs stream discriminator
  // (see vector.yaml _stream_fields); `service`/`runtime` route and group the
  // fleet; `timestamp` (filled by the timestamp() format below) is the line's
  // _time. None may be redefined by a caller whose meta happens to reuse one of
  // these names — winston merges caller meta OVER defaultMeta, so without this
  // a per-call field wins.
  const RESERVED = {
    logger: "app",
    service: resolveServiceName(),
    runtime: process.versions && process.versions.bun ? "bun" : "node",
  };

  // Reclaim the reserved names from caller meta. Motivating incident: an app
  // logged { timestamp } holding an 18-digit YYYYMMDDHHmmssSSSS signature stamp
  // (a business value, not a wall clock). winston's timestamp() format only
  // fills `timestamp` when absent, so that value reached stdout, and
  // VictoriaLogs (_time_field=timestamp) read the 18-digit number as a
  // nanosecond epoch => 1976-06-03 — below the retention floor, so the line was
  // silently DROPPED. Re-assert our values and preserve any caller collision
  // under `<name>_field`, so the datum is still logged, just not as a reserved
  // field. Runs before timestamp() so the real time is stamped after we clear
  // theirs.
  const reserveFields = winston.format((info) => {
    if ("timestamp" in info) {
      info.timestamp_field = info.timestamp;
      delete info.timestamp;
    }
    for (const key of Object.keys(RESERVED)) {
      if (key in info && info[key] !== RESERVED[key]) info[key + "_field"] = info[key];
      info[key] = RESERVED[key];
    }
    return info;
  });

  const format = pretty
    ? winston.format.combine(
        reserveFields(),
        winston.format.errors({ stack: true }),
        serializeErrors(),
        winston.format.colorize(),
        winston.format.timestamp({ format: "HH:mm:ss.SSS" }),
        winston.format.printf(({ timestamp, level, message, stack, logger: _l, service: _s, runtime: _r, ...meta }) => {
          const rest = Object.keys(meta).length ? ` ${JSON.stringify(meta)}` : "";
          return `${timestamp} ${level} ${message}${rest}${stack ? `\n${stack}` : ""}`;
        }),
      )
    : winston.format.combine(
        reserveFields(),
        winston.format.errors({ stack: true }),
        serializeErrors(),
        winston.format.timestamp(),
        winston.format.json(),
      );

  // An invalid LOG_LEVEL must fall back loudly, not silently mute the
  // logger (external QA: LOG_LEVEL=banana silenced even audit).
  let level = process.env.LOG_LEVEL || "info";
  if (!(level in LEVELS)) {
    console.warn(`[telemetry] invalid LOG_LEVEL "${level}" — using "info" (valid: ${Object.keys(LEVELS).join("|")})`);
    level = "info";
  }

  return winston.createLogger({
    levels: LEVELS,
    level,
    // logger/service/runtime come from RESERVED (and are re-asserted per line
    // by reserveFields, so caller meta can never clobber them). `logger: "app"`
    // is the stream discriminator — see vector.yaml's _stream_fields.
    // Convention: http | app | (absent).
    defaultMeta: { ...RESERVED },
    format,
    // handleExceptions/handleRejections: uncaught exceptions and unhandled
    // rejections are logged as ONE structured line (same format, logger=app,
    // service, stack) instead of a raw multi-line stack on stderr — which
    // the line-based Fly log stream would split into N unqueryable records.
    // winston's default exitOnError=true stays: the process still dies after
    // the line is written (crash-only; Fly restarts the machine).
    transports: [new winston.transports.Console({ handleExceptions: true, handleRejections: true })],
  });
}

function attachLogtail(instance) {
  try {
    const transport = makeLogtailTransport();
    if (transport) {
      instance.add(transport);
      console.log("[telemetry] app logs also shipping to Logtail (LOGTAIL_URL set; http lines excluded)");
    }
  } catch (error) {
    console.error("[telemetry] Logtail transport failed to attach, continuing without it", error);
  }
  return instance;
}

/**
 * Build (once) and return the real app logger. init() calls this at
 * activation so the crash handlers are registered from boot — a startup
 * crash before the first logger.info() call is still captured.
 */
function ensure() {
  if (!realLogger) realLogger = attachLogtail(buildLogger());
  return realLogger;
}

/**
 * Lazy singleton behind a Proxy: `logger.info(...)` / `logger.audit(...)`
 * just work anywhere; winston isn't loaded until first use (or init(),
 * whichever comes first).
 */
const logger = new Proxy(
  {},
  {
    get(_target, prop) {
      const instance = ensure();
      const value = instance[prop];
      return typeof value === "function" ? value.bind(instance) : value;
    },
  },
);

module.exports = { logger, ensure };

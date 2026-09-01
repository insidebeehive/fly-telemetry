"use strict";
/**
 * The app + audit loggers — `logger` and `audit` from the package root.
 * Zero config:
 *
 *   const { logger, audit } = require("@insidebeehive/telemetry");
 *   logger.info("bet placed", { betId, amount });          // logger=app
 *   audit.info("bet.settled", { actor, userId, amount });  // logger=audit
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
let realAudit = null;

function buildLogger(stream, { level, handleCrashes }) {
  const winston = require("winston");
  const pretty = !process.env.FLY_APP_NAME && process.env.NODE_ENV !== "production";

  // Errors nested in meta — logger.error("payment failed", { err, orderId })
  // — would JSON.stringify to {} (Error props are non-enumerable). Serialise
  // them so the stack always lands in the line. Top-level Errors
  // (logger.error(err)) are handled by winston's errors({stack}) format.
  const serializeErrors = winston.format((info) => {
    for (const key of Object.keys(info)) {
      const value = info[key];
      if (value instanceof Error) {
        info[key] = {
          message: value.message,
          stack: value.stack,
          ...(value.code !== undefined ? { code: value.code } : {}),
        };
      }
    }
    return info;
  });

  const format = pretty
    ? winston.format.combine(
        winston.format.errors({ stack: true }),
        serializeErrors(),
        winston.format.colorize(),
        winston.format.timestamp({ format: "HH:mm:ss.SSS" }),
        winston.format.printf(({ timestamp, level, message, stack, logger: _l, service: _s, ...meta }) => {
          const rest = Object.keys(meta).length ? ` ${JSON.stringify(meta)}` : "";
          return `${timestamp} ${level} ${message}${rest}${stack ? `\n${stack}` : ""}`;
        }),
      )
    : winston.format.combine(
        winston.format.errors({ stack: true }),
        serializeErrors(),
        winston.format.timestamp(),
        winston.format.json(),
      );

  return winston.createLogger({
    level,
    // The `logger` field is the stream discriminator — see vector.yaml's
    // _stream_fields. Convention: http | app | audit | (absent).
    defaultMeta: { logger: stream, service: resolveServiceName() },
    format,
    // handleExceptions/handleRejections (app logger only): uncaught
    // exceptions and unhandled rejections are logged as ONE structured line
    // (same format, logger=app, service, stack) instead of a raw multi-line
    // stack on stderr — which the line-based Fly log stream would split into
    // N unqueryable records. winston's default exitOnError=true stays: the
    // process still dies after the line is written (crash-only; Fly
    // restarts the machine).
    transports: [new winston.transports.Console(handleCrashes ? { handleExceptions: true, handleRejections: true } : {})],
  });
}

/**
 * Build (once) and return the real app logger. init() calls this at
 * activation so the crash handlers are registered from boot — a startup
 * crash before the first logger.info() call is still captured.
 */
function ensure() {
  if (!realLogger) realLogger = buildLogger("app", { level: process.env.LOG_LEVEL || "info", handleCrashes: true });
  return realLogger;
}

// The audit logger's level is FIXED at info, deliberately not LOG_LEVEL:
// an app quieted to LOG_LEVEL=error must still record every audit event.
// No crash handlers here — that is the app logger's job.
function ensureAudit() {
  if (!realAudit) realAudit = buildLogger("audit", { level: "info", handleCrashes: false });
  return realAudit;
}

const lazy = (build) =>
  new Proxy(
    {},
    {
      get(_target, prop) {
        const instance = build();
        const value = instance[prop];
        return typeof value === "function" ? value.bind(instance) : value;
      },
    },
  );

/**
 * Lazy singletons behind Proxies: `logger.info(...)` / `audit.info(...)`
 * just work anywhere; winston isn't loaded until first use (or init(),
 * whichever comes first).
 */
const logger = lazy(ensure);
const audit = lazy(ensureAudit);

module.exports = { logger, audit, ensure };

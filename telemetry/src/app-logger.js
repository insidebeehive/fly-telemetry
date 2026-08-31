"use strict";
/**
 * The app logger — `logger` from the package root. Zero config:
 *
 *   const { logger } = require("@insidebeehive/telemetry");   // or import
 *   logger.info("bet placed", { betId, amount });
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

function buildLogger() {
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
    level: process.env.LOG_LEVEL || "info",
    // `logger: "app"` is the whole point — see vector.yaml's _stream_fields.
    defaultMeta: { logger: "app", service: resolveServiceName() },
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

/**
 * Build (once) and return the real logger. init() calls this at activation
 * so the crash handlers are registered from boot — a startup crash before
 * the first logger.info() call is still captured as a structured line.
 */
function ensure() {
  if (!realLogger) realLogger = buildLogger();
  return realLogger;
}

/**
 * Lazy singleton behind a Proxy: `logger.info(...)` just works anywhere,
 * and winston isn't loaded until first use (or init(), whichever is first).
 */
const logger = new Proxy(
  {},
  {
    get(_target, prop) {
      const value = ensure()[prop];
      return typeof value === "function" ? value.bind(realLogger) : value;
    },
  },
);

module.exports = { logger, ensure };

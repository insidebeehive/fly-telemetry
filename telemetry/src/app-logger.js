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
 * - LOG_LEVEL env sets the level (default info).
 * - logger.child({ module: "wallet" }) works as normal winston.
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
  const format = pretty
    ? winston.format.combine(
        winston.format.errors({ stack: true }),
        winston.format.colorize(),
        winston.format.timestamp({ format: "HH:mm:ss.SSS" }),
        winston.format.printf(({ timestamp, level, message, logger: _l, service: _s, ...meta }) => {
          const rest = Object.keys(meta).length ? ` ${JSON.stringify(meta)}` : "";
          return `${timestamp} ${level} ${message}${rest}`;
        }),
      )
    : winston.format.combine(
        winston.format.errors({ stack: true }),
        winston.format.timestamp(),
        winston.format.json(),
      );

  return winston.createLogger({
    level: process.env.LOG_LEVEL || "info",
    // `logger: "app"` is the whole point — see vector.yaml's _stream_fields.
    defaultMeta: { logger: "app", service: resolveServiceName() },
    format,
    transports: [new winston.transports.Console()],
  });
}

/**
 * Lazy singleton behind a Proxy: `logger.info(...)` just works anywhere,
 * and winston isn't loaded until the first call.
 */
const logger = new Proxy(
  {},
  {
    get(_target, prop) {
      if (!realLogger) realLogger = buildLogger();
      const value = realLogger[prop];
      return typeof value === "function" ? value.bind(realLogger) : value;
    },
  },
);

module.exports = { logger };

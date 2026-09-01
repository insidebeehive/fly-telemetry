import type { Logger, LeveledLogMethod } from "winston";

/**
 * Activate telemetry programmatically (OTel tracing + HTTP logging + app
 * logger crash handlers). Idempotent — a no-op when the package was already
 * activated via NODE_OPTIONS="--import @insidebeehive/telemetry/register".
 */
export declare function init(): void;

export interface AppLogger extends Logger {
  /**
   * Audit level — priority 0 (above error), so LOG_LEVEL can never silence
   * it. Lines stay in the logger=app stream with level=audit; query with
   * `_stream:{logger="app"} level:audit`.
   *
   *   logger.audit("order.settled", { actor, userId, orderId, amount });
   *
   * Durability note: the log pipeline is best-effort (lines in flight
   * during a machine restart can be lost) and retention is the log
   * store's. For regulatory audit trails, keep the database as the system
   * of record; this is the queryable/correlatable copy.
   */
  audit: LeveledLogMethod;
}

/**
 * Zero-config app logger (winston). JSON on Fly/production, pretty locally.
 * Every line carries logger=app + service; trace_id/span_id are injected
 * automatically inside requests. LOG_LEVEL env sets the level (audit lines
 * are exempt).
 *
 *   logger.info("order placed", { orderId, amount });
 *   logger.error("payment capture failed", { err, orderId });
 *   logger.audit("order.settled", { actor, userId, orderId, amount });
 *   const walletLog = logger.child({ module: "wallet" });
 */
export declare const logger: AppLogger;

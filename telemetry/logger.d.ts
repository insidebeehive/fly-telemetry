import type { AppLogger } from "./index";

/**
 * Zero-config app logger (winston) — the default-export form of the
 * package root's named `logger` export; both resolve to the same
 * singleton. JSON in production, pretty locally; every line carries
 * logger=app + service; trace_id/span_id are injected automatically
 * inside requests; `logger.audit(...)` can never be silenced by
 * LOG_LEVEL.
 *
 *   import logger from "@insidebeehive/telemetry/logger";
 *
 *   logger.info("order placed", { orderId, amount });
 *   logger.error("payment capture failed", { err, orderId });
 *   logger.audit("order.settled", { actor, userId, orderId, amount });
 */
declare const logger: AppLogger;
export default logger;

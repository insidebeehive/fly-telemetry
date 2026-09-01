import type { Logger } from "winston";

/**
 * Activate telemetry programmatically (OTel tracing + HTTP logging + app
 * logger crash handlers). Idempotent — a no-op when the package was already
 * activated via NODE_OPTIONS="--import @insidebeehive/telemetry/register".
 */
export declare function init(): void;

/**
 * Zero-config app logger (winston). JSON on Fly/production, pretty locally.
 * Every line carries logger=app + service; trace_id/span_id are injected
 * automatically inside requests. LOG_LEVEL env sets the level.
 *
 *   logger.info("bet placed", { betId, amount });
 *   logger.error("payment capture failed", { err, orderId });
 *   const walletLog = logger.child({ module: "wallet" });
 */
export declare const logger: Logger;

"use strict";
/**
 * Default-export convenience subpath:
 *
 *   import logger from "@insidebeehive/telemetry/logger";      // ESM / TS
 *   const logger = require("@insidebeehive/telemetry/logger"); // CJS
 *
 * The exact same lazy singleton as the package root's named export
 * (`import { logger } from "@insidebeehive/telemetry"`) — both forms stay
 * supported. Importing this does NOT activate tracing/HTTP logging; that
 * remains register / init()'s job.
 */
module.exports = require("./index.js").logger;

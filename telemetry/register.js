"use strict";
/**
 * CJS preload entry — what `--require @insidebeehive/telemetry/register`
 * (or the "require" condition of `--import` on a CJS-resolving toolchain)
 * loads. CJS apps need no loader hook: the OTel instrumentations patch
 * require() calls via require-in-the-middle on their own.
 */
require("./index.js").init();

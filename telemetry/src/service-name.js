"use strict";
/**
 * service.name resolution, shared by tracing and the http logger so spans
 * and http.access lines always agree:
 *
 *   OTEL_SERVICE_NAME > FLY_APP_NAME > app's own package.json name > "unknown-service"
 *
 * The package.json read is best-effort from the process working directory —
 * correct under the fleet convention that CMD execs node from the app root.
 */

const fs = require("node:fs");
const path = require("node:path");

function packageName() {
  try {
    const raw = fs.readFileSync(path.join(process.cwd(), "package.json"), "utf8");
    const name = JSON.parse(raw).name;
    return typeof name === "string" && name.trim() ? name.trim() : undefined;
  } catch {
    return undefined;
  }
}

function resolveServiceName() {
  return process.env.OTEL_SERVICE_NAME || process.env.FLY_APP_NAME || packageName() || "unknown-service";
}

module.exports = { resolveServiceName };

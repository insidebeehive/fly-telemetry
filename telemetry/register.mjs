/**
 * ESM entry — what `--import @insidebeehive/telemetry/register` loads.
 *
 * ESM apps (Remix/Vite server builds) need a module-loader hook so the OTel
 * instrumentations can wrap `import`ed modules; require() patching alone
 * cannot see them. This registers import-in-the-middle's hook (via the
 * supported @opentelemetry/instrumentation re-export) BEFORE starting the
 * SDK, using IITM's message channel so module wrapping is acknowledged
 * before the app's own imports execute — the same bootstrap pattern
 * dd-trace ships for its `--import dd-trace/register` entry.
 *
 * CJS apps can use this entry too: the hook is harmless there, and init()
 * covers the require() side. One flag for the whole fleet.
 *
 * If hook registration fails, we log and continue: CJS instrumentation and
 * the diagnostics_channel-based http logger still work; only ESM `import`
 * patching (express/winston spans inside an ESM app) is degraded.
 */
import { createRequire } from "node:module";
import * as nodeModule from "node:module";

const require = createRequire(import.meta.url);

let waitForAllMessagesAcknowledged;

if (typeof nodeModule.register === "function") {
  try {
    const { createAddHookMessageChannel } = require("import-in-the-middle");
    const channel = createAddHookMessageChannel();
    nodeModule.register("@opentelemetry/instrumentation/hook.mjs", import.meta.url, channel.registerOptions);
    waitForAllMessagesAcknowledged = channel.waitForAllMessagesAcknowledged;
  } catch (error) {
    console.error("[telemetry] ESM loader hook registration failed; ESM imports will not be traced (CJS and node:http capture unaffected)", error);
  }
} else {
  console.error("[telemetry] node:module.register unavailable (Node < 20.6?); ESM imports will not be traced");
}

require("./index.js").init();

if (waitForAllMessagesAcknowledged) {
  await waitForAllMessagesAcknowledged();
}

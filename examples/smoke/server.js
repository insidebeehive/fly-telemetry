"use strict";
/**
 * Smoke-test app (CJS entry) for @insidebeehive/telemetry.
 * No telemetry code in here beyond using the exported logger —
 * activation happens via NODE_OPTIONS. Routes exercise every feature:
 *
 *   GET  /health  -> must NOT be logged or traced (ignore list)
 *   GET  /hello   -> plain request (access line only under default policy)
 *   POST /bets    -> JSON body in/out, logger.info + logger.audit inside the
 *                    request (trace_id join), token field must be [REDACTED]
 *   GET  /error   -> 500 (payload attaches under the default errors policy)
 *   GET  /slow    -> 1.2s (slow tier of the errors policy)
 *   GET  /crash   -> uncaught exception (crash handler -> one line -> exit)
 *   GET  /reject  -> unhandled rejection (same)
 */
const express = require("express");
const { logger } = require("@insidebeehive/telemetry");

const app = express();
app.use(express.json());

app.get("/health", (_req, res) => res.send("ok"));

app.get("/hello", (_req, res) => {
  logger.info("hello handled", { greeting: "world" });
  res.json({ hello: "world" });
});

app.post("/bets", (req, res) => {
  const { amount, gameId } = req.body || {};
  logger.info("bet placed", { amount, gameId });
  logger.audit("bet.placed", { actor: "smoke-test", userId: "u_test", amount, gameId });
  res.json({ betId: "b_smoke_1", status: "accepted", amount });
});

app.get("/error", (_req, res) => {
  logger.error("provider timeout", { err: new Error("upstream 504"), provider: "acme" });
  res.status(502).json({ error: "provider_timeout" });
});

app.get("/slow", async (_req, res) => {
  await new Promise((r) => setTimeout(r, 1200));
  res.json({ slow: true });
});

app.get("/crash", (_req, res) => {
  res.json({ crashing: true });
  setTimeout(() => {
    throw new Error("smoke uncaught exception");
  }, 50);
});

app.get("/reject", (_req, res) => {
  res.json({ rejecting: true });
  Promise.reject(new Error("smoke unhandled rejection"));
});

const port = process.env.PORT || 3000;
app.listen(port, () => logger.info("smoke app listening", { port: Number(port), entry: "cjs" }));

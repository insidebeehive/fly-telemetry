"use strict";
/**
 * Single source of truth for "what must never reach a log line or a span".
 * Ported from softstudio-bo apps/api/src/common/security/redact.util.ts so the
 * platform preload enforces the same policy the bo service already proved out.
 *
 * Deliberately dependency-free: this runs as part of a --require preload
 * before any framework exists.
 */

const REDACTED = "[REDACTED]";

/**
 * Matched against key NAMES, by substring, case-insensitively, after
 * normalisation. Over-redaction is the safe failure direction for a log line.
 * Deliberately absent: bare `key` (would take `cacheKey`, `keyword`) and bare
 * `pin` (would take `spinCount`, `pinned`) — the specific compounds are listed
 * instead, and the whole-word set below catches the bare forms.
 */
const SENSITIVE_KEY_PATTERN =
  /(password|passwd|pwd|token|secret|otp|mpin|hashkey|hash_key|saltkey|salt_key|signature|authorization|cookie|credential|apikey|api_key|privatekey|private_key|accesskey|access_key|accountnumber|account_no|ifsc|cvv|cardnumber|card_no|creditcard|credit_card)/i;

/**
 * Keys are normalised before matching: non-alphanumerics stripped, so
 * `x-api-key`, `salt_key` and `saltKey` all collapse to the same form.
 */
const normaliseKey = (key) => key.replace(/[^a-zA-Z0-9]/g, "");

/** Sensitive only as a WHOLE word — as substrings they would over-match. */
const EXACT_SENSITIVE_KEYS = new Set(["pin", "pan", "iban", "sig"]);

/** THE test for "must this value be redacted?" — always use this, not the raw pattern. */
const isSensitiveKey = (key) => {
  const normalised = normaliseKey(String(key));
  return EXACT_SENSITIVE_KEYS.has(normalised.toLowerCase()) || SENSITIVE_KEY_PATTERN.test(normalised);
};

/** The body and query are untrusted input, so recursion needs a floor. */
const MAX_DEPTH = 6;

/**
 * Deep-copies `value`, replacing any value whose KEY matches the sensitive
 * test with REDACTED. Structure is preserved so the shape of a payload stays
 * debuggable while its secrets do not survive.
 */
const redactSensitive = (value, depth = 0) => {
  if (depth >= MAX_DEPTH) return "[TRUNCATED]";
  if (Array.isArray(value)) return value.map((item) => redactSensitive(item, depth + 1));
  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.entries(value).map(([key, val]) => [key, isSensitiveKey(key) ? REDACTED : redactSensitive(val, depth + 1)]),
    );
  }
  return value;
};

/**
 * Wholesale query strip — for URLs where no param can be assumed safe
 * (provider callbacks carry one-time tokens and signatures in the query).
 */
const stripQuery = (url) => {
  const cut = url.indexOf("?");
  return cut === -1 ? url : `${url.slice(0, cut)}?${REDACTED}`;
};

/**
 * Per-key redaction of a URL's query string, keeping harmless params (page,
 * filters) legible. Used on ordinary application routes where the query is
 * what makes the log line useful.
 */
const redactUrl = (originalUrl) => {
  const [pathPart, qs] = String(originalUrl).split("?");
  if (!qs) return originalUrl;
  try {
    const params = new URLSearchParams(qs);
    for (const key of Array.from(params.keys())) {
      if (isSensitiveKey(key)) params.set(key, REDACTED);
    }
    return `${pathPart}?${params.toString().replace(/%5BREDACTED%5D/g, REDACTED)}`;
  } catch {
    // Unparseable query — safer to drop it than to log it raw.
    return `${pathPart}?${REDACTED}`;
  }
};

/**
 * Headers worth logging, as an ALLOWLIST. A denylist has to be extended every
 * time an auth header is added and is silently wrong in the window before
 * someone notices; naming what is safe fails closed instead.
 */
const SAFE_HEADERS = ["host", "content-type", "content-length", "user-agent", "referer", "origin", "accept-language", "x-forwarded-for", "userid", "operatorid", "traceparent"];

const pickHeaders = (headers, allowed = SAFE_HEADERS) => {
  if (!headers) return {};
  const out = {};
  for (const name of allowed) {
    if (headers[name] !== undefined) out[name] = headers[name];
  }
  return out;
};

module.exports = { REDACTED, SENSITIVE_KEY_PATTERN, isSensitiveKey, redactSensitive, stripQuery, redactUrl, SAFE_HEADERS, pickHeaders };

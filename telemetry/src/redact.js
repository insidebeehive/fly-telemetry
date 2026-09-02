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
  /(password|passwd|pwd|token|secret|otp|mpin|hashkey|hash_key|saltkey|salt_key|signature|authorization|cookie|credential|apikey|api_key|privatekey|private_key|accesskey|access_key|accountnumber|account_no|ifsc|cvv|cvc|ssn|cardnumber|cardno|card_no|accountno|creditcard|credit_card)/i;

/**
 * Keys are normalised before matching: non-alphanumerics stripped, so
 * `x-api-key`, `salt_key` and `saltKey` all collapse to the same form.
 */
const normaliseKey = (key) => key.replace(/[^a-zA-Z0-9]/g, "");

/** Sensitive only as a WHOLE word — as substrings they would over-match. */
const EXACT_SENSITIVE_KEYS = new Set(["pin", "pan", "iban", "sig", "card"]);

/** THE test for "must this value be redacted?" — always use this, not the raw pattern. */
const isSensitiveKey = (key) => {
  const normalised = normaliseKey(String(key));
  return EXACT_SENSITIVE_KEYS.has(normalised.toLowerCase()) || SENSITIVE_KEY_PATTERN.test(normalised);
};

/**
 * Value-level PAN scrubbing: a 13-19 digit run that passes Luhn is treated
 * as a card number wherever it appears, regardless of its key. Catches PANs
 * under innocuous keys and inside raw text. Over-redaction (a rare numeric
 * id that happens to pass Luhn) is the safe failure direction.
 */
const luhnOk = (digits, start = 0, end = digits.length) => {
  let sum = 0;
  let dbl = false;
  for (let i = end - 1; i >= start; i--) {
    let d = digits.charCodeAt(i) - 48;
    if (dbl) {
      d *= 2;
      if (d > 9) d -= 9;
    }
    sum += d;
    dbl = !dbl;
  }
  return sum % 10 === 0;
};
// A PAN glued to extra digits forms a longer run whose WHOLE fails Luhn, so
// the run must be scanned for any Luhn-valid 13-19 digit WINDOW — the
// whole-run check let `<PAN>0000` under an innocent key log a fully
// recoverable card number (external QA round 4). When a window hits, the
// ENTIRE run is redacted: leaving the adjacent digits would leak which part
// was the PAN. Index-based luhnOk keeps this allocation-free (~7 windows x
// run length worst case — sub-millisecond even on a 4KB digit run).
const containsLuhnWindow = (digits) => {
  const n = digits.length;
  for (let len = 13; len <= 19 && len <= n; len++) {
    for (let i = 0; i + len <= n; i++) {
      if (luhnOk(digits, i, i + len)) return true;
    }
  }
  return false;
};
const redactPANs = (text) => String(text).replace(/(?<!\d)\d{13,}(?!\d)/g, (m) => (containsLuhnWindow(m) ? "[REDACTED-PAN]" : m));

/**
 * Best-effort scrub for RAW text that could not be parsed (truncated JSON,
 * malformed JSON, unknown text bodies): redact values of sensitive keys in
 * JSON-ish and urlencoded-ish shapes, then PAN-scrub. Never returns raw
 * unscrubbed input — this is the backstop that keeps "cap then parse-fail"
 * from ever leaking a credential verbatim.
 */
const decodeSafe = (s) => {
  try {
    return decodeURIComponent(s);
  } catch {
    return s;
  }
};
const scrubText = (text) => {
  // NUL bytes are never legitimate in textual payloads; NUL-interleaved text
  // (utf-16 bytes behind a lying utf-8 charset) would otherwise slip digits
  // and key names past every regex below.
  let out = String(text).replace(/\u0000/g, "");
  const K = String.raw`(?:\\.|[^"\\]){1,256}`; //  double-quoted key: escapes ok, long keys ok
  const KQ = String.raw`(?:\\.|[^'\\]){1,256}`; // single-quoted key
  // "key": "value" / 'key': 'value' (escaped quotes allowed in value)
  out = out.replace(new RegExp(`"(${K})"\\s*:\\s*"(?:\\\\.|[^"\\\\])*"`, "g"), (m, key) => (isSensitiveKey(key) ? `"${key}":"[REDACTED]"` : m));
  out = out.replace(new RegExp(`'(${KQ})'\\s*:\\s*'(?:\\\\.|[^'\\\\])*'`, "g"), (m, key) => (isSensitiveKey(key) ? `'${key}':'[REDACTED]'` : m));
  // unterminated string values running to end of capped text (a trailing
  // lone backslash from a mid-escape cut is allowed)
  out = out.replace(new RegExp(`"(${K})"\\s*:\\s*"(?:\\\\.|[^"\\\\])*\\\\?$`, "g"), (m, key) => (isSensitiveKey(key) ? `"${key}":"[REDACTED]"` : m));
  out = out.replace(new RegExp(`'(${KQ})'\\s*:\\s*'(?:\\\\.|[^'\\\\])*\\\\?$`, "g"), (m, key) => (isSensitiveKey(key) ? `'${key}':'[REDACTED]'` : m));
  // "key": [array...] (possibly truncated) — redact the whole array value
  out = out.replace(new RegExp(`"(${K})"\\s*:\\s*\\[[^\\]]*(?:\\]|$)`, "g"), (m, key) => (isSensitiveKey(key) ? `"${key}":"[REDACTED]"` : m));
  // "key": bareword/number/bool
  out = out.replace(new RegExp(`"(${K})"\\s*:\\s*([^",{}\\[\\]\\s][^,}\\]]*)`, "g"), (m, key) => (isSensitiveKey(key) ? `"${key}":"[REDACTED]"` : m));
  // urlencoded / query-ish pairs
  out = out.replace(/(^|[&?])([^&=?\s]{1,256})=([^&\s]*)/g, (m, sep, key) => (isSensitiveKey(decodeSafe(key)) ? `${sep}${key}=[REDACTED]` : m));
  // bare key[:=]value anywhere (plain-text shapes: "password: x", mid-text
  // "token=x"). Lookbehind keeps the key unconsumed so adjacent pairs are
  // each evaluated. Over-matching is the safe direction.
  out = out.replace(/(?<=(?:^|[\s{,;&(])([A-Za-z0-9_.\-]{1,256})\s*[:=]\s*)[^\s&,;:="'{}\[\]]+/g, (value, key) => (isSensitiveKey(key) ? "[REDACTED]" : value));
  return redactPANs(out);
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
  if (typeof value === "string") return redactPANs(value);
  if (typeof value === "number" && Number.isInteger(value)) {
    const digits = String(Math.abs(value));
    if (digits.length >= 13 && containsLuhnWindow(digits)) return "[REDACTED-PAN]";
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
  // The PATH is scrubbed too: REST-style routes put card numbers between
  // slashes (/pay/<PAN>), which lands in every access line's path/url
  // fields (external QA round 5 — same class as the body finding).
  const safePath = redactPANs(pathPart);
  if (!qs) return safePath;
  try {
    const params = new URLSearchParams(qs);
    for (const key of Array.from(params.keys())) {
      if (isSensitiveKey(key)) params.set(key, REDACTED);
    }
    return `${safePath}?${redactPANs(params.toString().replace(/%5BREDACTED%5D/g, REDACTED))}`;
  } catch {
    // Unparseable query — safer to drop it than to log it raw.
    return `${safePath}?${REDACTED}`;
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
    if (headers[name] !== undefined) {
      let value = headers[name];
      if (typeof value === "string") {
        // referer is a URL by definition — full URL treatment (per-key query
        // redaction + path PAN scrub); every other value gets the PAN scrub
        // (external QA round 5: an app that puts card numbers in URLs leaks
        // them via referer too). Scrub BEFORE capping so the cap can't
        // slice a card number into an unrecognisable, unredacted prefix.
        value = name === "referer" ? redactUrl(value) : redactPANs(value);
        // Cap logged header VALUES (an 8KB user-agent must not amplify
        // every line); bodies have their own cap.
        if (value.length > 512) value = `${value.slice(0, 512)}…[+${value.length - 512} chars]`;
      }
      out[name] = value;
    }
  }
  return out;
};

module.exports = { REDACTED, SENSITIVE_KEY_PATTERN, isSensitiveKey, redactSensitive, stripQuery, redactUrl, SAFE_HEADERS, pickHeaders, scrubText, redactPANs };

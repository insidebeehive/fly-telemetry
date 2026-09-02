"use strict";
/**
 * Single source of truth for "what must never reach a log line or a span".
 *
 * POLICY (owner decision, 2026-09-02): logs are evidence. Business data —
 * amounts, ids, card/account numbers, bank/UPI transaction refs — is logged
 * VERBATIM. Redaction is reserved for material that grants access:
 *   - passwords / OTPs / PINs
 *   - session ids and session/access tokens
 *   - API keys, secrets, private/access keys, credentials
 *   - webhook signatures (and their hash/salt keys)
 *   - Authorization / Cookie — never logged at all (header ALLOWLIST below)
 *
 * Releases 0.1.7–0.1.11 additionally carried PCI-style Luhn card-number
 * scrubbing ([REDACTED-PAN], [CUT-DIGITS]). Removed deliberately: Luhn
 * false-positives redacted the very numeric refs these logs exist to keep,
 * and card-data visibility is an accepted, owner-signed trade-off. If that
 * changes, the 0.1.11 implementation in git history is the reference.
 *
 * Deliberately dependency-free: this runs as part of a --require preload
 * before any framework exists.
 */

const REDACTED = "[REDACTED]";

/**
 * Matched against key NAMES, by substring, case-insensitively, after
 * normalisation. Entries MUST be written in normalized (alphanumeric-only)
 * form — normaliseKey strips underscores/dashes BEFORE matching, so
 * `api_key`, `x-api-key` and `apiKey` all collapse to `apikey`.
 * `session` covers sessionid / session_key / jsessionid; `token` covers
 * access/refresh/session tokens. Over-redaction of a rare benign compound
 * (e.g. `sessionsCount`) is accepted — it is still the safe direction.
 */
const SENSITIVE_KEY_PATTERN =
  /(password|passwd|pwd|otp|mpin|session|token|secret|apikey|privatekey|accesskey|credential|authorization|cookie|signature|hashkey|saltkey)/i;

/**
 * Keys are normalised before matching: non-alphanumerics stripped, so
 * `x-api-key`, `salt_key` and `saltKey` all collapse to the same form.
 */
const normaliseKey = (key) => key.replace(/[^a-zA-Z0-9]/g, "");

/** Sensitive only as a WHOLE word — as substrings they would over-match
 *  (`pin` would take `spinCount`, `sig` would take `design`). */
const EXACT_SENSITIVE_KEYS = new Set(["pin", "sig"]);

/** THE test for "must this value be redacted?" — always use this, not the raw pattern. */
const isSensitiveKey = (key) => {
  const normalised = normaliseKey(String(key));
  return EXACT_SENSITIVE_KEYS.has(normalised.toLowerCase()) || SENSITIVE_KEY_PATTERN.test(normalised);
};

/**
 * Best-effort scrub for RAW text that could not be parsed (truncated JSON,
 * malformed JSON, unknown text bodies): redact values of sensitive keys in
 * JSON-ish and urlencoded-ish shapes. This is the backstop that keeps
 * "cap then parse-fail" from leaking a credential verbatim — a token whose
 * value is sliced by the byte cap is still caught by key.
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
  // (utf-16 bytes behind a lying utf-8 charset) would otherwise slip key
  // names past every regex below.
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
  return out;
};

/** The body and query are untrusted input, so recursion needs a floor. */
const MAX_DEPTH = 6;

/**
 * Deep-copies `value`, replacing any value whose KEY matches the sensitive
 * test with REDACTED. Structure is preserved so the shape of a payload stays
 * debuggable while credentials do not survive. Values under non-sensitive
 * keys pass through VERBATIM (policy above — evidence first).
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

/** Per-key redaction of a bare query string ("a=1&token=x" form). */
const redactQueryString = (qs) => {
  try {
    const params = new URLSearchParams(String(qs));
    for (const key of Array.from(params.keys())) {
      if (isSensitiveKey(key)) params.set(key, REDACTED);
    }
    return params.toString().replace(/%5BREDACTED%5D/g, REDACTED);
  } catch {
    // Unparseable query — safer to drop it than to log it raw.
    return REDACTED;
  }
};

/**
 * Per-key redaction of a URL's query string, keeping harmless params (page,
 * filters, refs, amounts) legible. The path is logged verbatim — it is
 * routing evidence (policy above).
 */
const redactUrl = (originalUrl) => {
  const [pathPart, qs] = String(originalUrl).split("?");
  if (!qs) return originalUrl;
  return `${pathPart}?${redactQueryString(qs)}`;
};

/**
 * Headers worth logging, as an ALLOWLIST. This is what makes Cookie and
 * Authorization structurally un-loggable: they are simply never picked. A
 * denylist has to be extended every time an auth header is added and is
 * silently wrong in the window before someone notices; naming what is safe
 * fails closed instead.
 */
const SAFE_HEADERS = ["host", "content-type", "content-length", "user-agent", "referer", "origin", "accept-language", "x-forwarded-for", "userid", "operatorid", "traceparent"];

const pickHeaders = (headers, allowed = SAFE_HEADERS) => {
  if (!headers) return {};
  const out = {};
  for (const name of allowed) {
    if (headers[name] !== undefined) {
      let value = headers[name];
      if (typeof value === "string") {
        // referer is a URL — its query gets the same per-key redaction as
        // the url field (a session token in a referer query is still a
        // session token).
        if (name === "referer") value = redactUrl(value);
        // Cap logged header VALUES (an 8KB user-agent must not amplify
        // every line); bodies have their own cap.
        if (value.length > 512) value = `${value.slice(0, 512)}…[+${value.length - 512} chars]`;
      }
      out[name] = value;
    }
  }
  return out;
};

module.exports = { REDACTED, SENSITIVE_KEY_PATTERN, isSensitiveKey, redactSensitive, stripQuery, redactQueryString, redactUrl, SAFE_HEADERS, pickHeaders, scrubText };

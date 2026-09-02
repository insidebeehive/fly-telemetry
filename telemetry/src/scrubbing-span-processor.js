"use strict";
/**
 * Last line of defence before spans are handed to the exporter. Ported from
 * softstudio-bo apps/api/src/common/tracing/scrubbing-span-processor.ts.
 *
 * The primary PII guarantee comes from configuration in tracing.js — pg and
 * mongodb run with enhancedDatabaseReporting off, the ioredis serialiser emits
 * command+key only, header capture is disabled. This processor exists because
 * that guarantee is only as good as the next dependency bump: it enforces
 * redaction structurally, so a changed default upstream degrades into a
 * scrubbed attribute rather than player financial data landing in Grafana.
 */

const { REDACTED, isSensitiveKey, redactUrl, redactQueryString } = require("./redact");

/** Attributes that carry a URL — their query strings get the same per-key
 *  redaction as the access line's url field (policy: paths and harmless
 *  params are evidence; session/token/key params are not). */
const URL_ATTRIBUTES = ["http.url", "url.full", "http.target", "url.path"];

const redactUrlAttribute = (value) => (typeof value === "string" ? redactUrl(value) : value);

class ScrubbingSpanProcessor {
  constructor(delegate) {
    this.delegate = delegate;
  }

  onStart(span, parentContext) {
    this.delegate.onStart(span, parentContext);
  }

  onEnd(span) {
    try {
      // `attributes` is typed readonly upstream but is a plain mutable object
      // at runtime; onEnd is the last point at which it can still be changed.
      const attributes = span.attributes;

      for (const key of Object.keys(attributes)) {
        // Header capture is disabled in instrumentation config; this makes a
        // re-enable (deliberate or via an upstream default change) harmless.
        if (key.startsWith("http.request.header.") || key.startsWith("http.response.header.")) {
          delete attributes[key];
          continue;
        }

        if (isSensitiveKey(key)) {
          attributes[key] = REDACTED;
          continue;
        }

        if (URL_ATTRIBUTES.includes(key)) {
          attributes[key] = redactUrlAttribute(attributes[key]);
        }
      }

      // url.query is a bare query string — per-key redaction, same policy
      // as the url field.
      if ("url.query" in attributes && typeof attributes["url.query"] === "string") {
        attributes["url.query"] = redactQueryString(attributes["url.query"]);
      }
    } catch {
      // A scrubbing failure must never drop the span or crash the exporter
      // pipeline; an unscrubbed span is still better than a dead process, and
      // the attributes it carries are the configured-safe set anyway.
    }

    this.delegate.onEnd(span);
  }

  forceFlush() {
    return this.delegate.forceFlush();
  }

  shutdown() {
    return this.delegate.shutdown();
  }
}

module.exports = { ScrubbingSpanProcessor };

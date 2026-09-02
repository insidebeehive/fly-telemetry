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

const { REDACTED, isSensitiveKey, stripQuery, redactPANs } = require("./redact");

/** Attributes that carry a URL — query strings are stripped wholesale, and
 *  the remaining path is PAN-scrubbed: REST routes like /pay/<PAN> would
 *  otherwise carry the card number into every exported span (same class as
 *  the access-line path finding, external QA round 5). */
const URL_ATTRIBUTES = ["http.url", "url.full", "http.target", "url.path"];

const stripQueryAttribute = (value) => (typeof value === "string" ? redactPANs(stripQuery(value)) : value);

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
          attributes[key] = stripQueryAttribute(attributes[key]);
        }
      }

      // url.query is the whole query string by definition — nothing in it is
      // worth the risk of inspecting it.
      if ("url.query" in attributes) {
        attributes["url.query"] = REDACTED;
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

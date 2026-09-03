using System.Diagnostics;
using OpenTelemetry;

namespace Beehive.Telemetry.Tracing;

/// <summary>
/// Last line of defence before spans are handed to the exporter.
/// </summary>
/// <remarks>
/// The primary guarantee comes from configuration — instrumentation header capture is off,
/// database statement enrichment is off. This processor exists because that guarantee is
/// only as good as the next dependency bump: it enforces redaction structurally, so a
/// changed default upstream degrades into a scrubbed attribute rather than credentials
/// landing in the trace store.
/// </remarks>
internal sealed class ScrubbingSpanProcessor : BaseProcessor<Activity>
{
    /// <summary>
    /// Attributes that carry a URL — their query strings get the same per-key redaction as
    /// the access line's url field (policy: paths and harmless params are evidence;
    /// session/token/key params are not).
    /// </summary>
    private static readonly HashSet<string> UrlAttributes = new(StringComparer.Ordinal)
    {
        "http.url", "url.full", "http.target", "url.path",
    };

    public override void OnEnd(Activity data)
    {
        if (data is null)
        {
            return;
        }

        try
        {
            // Snapshot first: SetTag mutates the very collection being enumerated.
            List<KeyValuePair<string, object?>>? tags = null;
            foreach (var tag in data.TagObjects)
            {
                (tags ??= []).Add(tag);
            }

            if (tags is null)
            {
                return;
            }

            foreach (var tag in tags)
            {
                var key = tag.Key;

                // Header capture is disabled in the instrumentation config; this makes a
                // re-enable (deliberate, or via an upstream default change) harmless.
                if (key.StartsWith("http.request.header.", StringComparison.Ordinal)
                    || key.StartsWith("http.response.header.", StringComparison.Ordinal))
                {
                    data.SetTag(key, null);
                    continue;
                }

                if (Redaction.IsSensitiveKey(key))
                {
                    data.SetTag(key, Redaction.Redacted);
                    continue;
                }

                if (UrlAttributes.Contains(key) && tag.Value is string url)
                {
                    data.SetTag(key, Redaction.RedactUrl(url));
                    continue;
                }

                // url.query is a bare query string — per-key redaction, same policy as the
                // url field.
                if (string.Equals(key, "url.query", StringComparison.Ordinal) && tag.Value is string query)
                {
                    data.SetTag(key, Redaction.RedactQueryString(query));
                }
            }
        }
        catch (Exception)
        {
            // A scrubbing failure must never drop the span or crash the exporter pipeline;
            // an unscrubbed span is still better than a dead process, and the attributes it
            // carries are the configured-safe set anyway.
        }
    }
}

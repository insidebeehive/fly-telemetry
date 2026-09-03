using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Beehive.Telemetry.Http;

/// <summary>
/// Turns captured body bytes into a log field. Port of <c>renderBody</c> from
/// <c>telemetry/src/http-logger.js</c>.
/// </summary>
/// <remarks>
/// Bodies become log text only when they are safely textual:
/// compressed bytes are NEVER decoded (a capped prefix of a gzip stream cannot be
/// decompressed anyway) — a size placeholder is logged instead; responses must be JSON
/// (which keeps streamed HTML documents out of the payload lines) while requests may also
/// be text/* or urlencoded; JSON is parsed and field-redacted before logging.
/// </remarks>
internal static class BodyRenderer
{
    /// <summary>
    /// The regex scrubbers can only see through ASCII-compatible bytes. A body in any other
    /// DECLARED charset (utf-16/32, ...) gets a size placeholder like compressed bytes do —
    /// utf-16le JSON otherwise sails past every scrub NUL-by-NUL.
    /// </summary>
    private static readonly HashSet<string> AsciiCompatibleCharsets = new(StringComparer.Ordinal)
    {
        "utf-8", "utf8", "us-ascii", "ascii", "iso-8859-1", "iso8859-1", "latin1", "latin-1", "windows-1252",
    };

    private static readonly Regex CharsetPattern = new(
        "charset\\s*=\\s*\"?([a-z0-9_\\-]+)\"?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex ContentLengthPattern = new(
        @"^\d{1,15}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Renders a captured body, or <see langword="null"/> when there is nothing to render.
    /// </summary>
    /// <param name="captured">The kept prefix of the body (at most the configured cap).</param>
    /// <param name="total">Total body size in bytes, including anything beyond the cap.</param>
    /// <param name="contentType">Declared content type, or <see langword="null"/>.</param>
    /// <param name="contentEncoding">Declared content encoding, or <see langword="null"/>.</param>
    /// <param name="jsonOnly">Responses render only JSON; requests also render text/* and urlencoded.</param>
    /// <param name="bodyMode">Whether parsed JSON stays a string or lands as nested fields.</param>
    internal static JsonNode? Render(
        ReadOnlySpan<byte> captured,
        long total,
        string? contentType,
        string? contentEncoding,
        bool jsonOnly,
        BodyMode bodyMode)
    {
        if (total == 0)
        {
            return null;
        }

        var encoding = (contentEncoding ?? string.Empty).ToLowerInvariant();
        if (encoding.Length > 0 && !string.Equals(encoding, "identity", StringComparison.Ordinal))
        {
            return JsonValue.Create(Placeholder(encoding, total));
        }

        var contentTypeLower = (contentType ?? string.Empty).ToLowerInvariant();
        var charset = CharsetPattern.Match(contentTypeLower);
        if (charset.Success && !AsciiCompatibleCharsets.Contains(charset.Groups[1].Value))
        {
            return JsonValue.Create(string.Create(
                CultureInfo.InvariantCulture,
                $"[{FirstMediaType(contentTypeLower, "text")} {charset.Groups[1].Value} {total} bytes]"));
        }

        var textual = jsonOnly
            ? contentTypeLower.Contains("json", StringComparison.Ordinal)
            : contentTypeLower.Contains("json", StringComparison.Ordinal)
                || contentTypeLower.StartsWith("text/", StringComparison.Ordinal)
                || contentTypeLower.Contains("urlencoded", StringComparison.Ordinal)
                || contentTypeLower.Length == 0;

        if (!textual)
        {
            return JsonValue.Create(Placeholder(FirstMediaType(contentTypeLower, "binary"), total));
        }

        // NUL bytes are never legitimate in textual bodies — strip them so NUL-interleaved
        // digits/keys (utf-16 bytes behind a LYING utf-8 charset) cannot slip past the
        // scrubbers below.
        var text = Encoding.UTF8.GetString(captured).Replace("\0", string.Empty, StringComparison.Ordinal);

        if (contentTypeLower.Contains("urlencoded", StringComparison.Ordinal))
        {
            // Login/payment form body: DECODE into a key/value object so it reads like a JSON
            // body (no %XX escapes, no "+" for spaces) and is queryable the same way, instead
            // of a raw "a=1&b=%20c" string. Per-key redaction, same policy as JSON bodies;
            // repeated keys collapse to an array so nothing is lost.
            try
            {
                var formObject = Redaction.RedactFormToObject(text);
                return bodyMode == BodyMode.String ? JsonValue.Create(Redaction.ToJsonString(formObject)) : formObject;
            }
            catch (Exception)
            {
                return JsonValue.Create(Redaction.ScrubText(text));
            }
        }

        if (contentTypeLower.Contains("json", StringComparison.Ordinal) || contentTypeLower.Length == 0)
        {
            JsonNode? parsed;
            try
            {
                parsed = Redaction.RedactJson(JsonNode.Parse(text));
            }
            catch (Exception)
            {
                // Truncated or malformed JSON — NEVER return it raw: best-effort
                // key/value scrub instead.
                return JsonValue.Create(Redaction.ScrubText(text));
            }

            return bodyMode == BodyMode.String ? JsonValue.Create(Redaction.ToJsonString(parsed)) : parsed;
        }

        return JsonValue.Create(Redaction.ScrubText(text));
    }

    /// <summary>
    /// Request bodies deliberately never read (compressed, multipart, other binary) still get
    /// size evidence on enriched lines. content-length is the only safe source — the stream
    /// was never tapped.
    /// </summary>
    internal static string? RequestPlaceholder(string? method, string? contentType, string? contentEncoding, string? contentLength)
    {
        if (HttpLogOptions.IsBodyless(method))
        {
            return null;
        }

        var encoding = (contentEncoding ?? string.Empty).ToLowerInvariant();
        var mediaType = FirstMediaType((contentType ?? string.Empty).ToLowerInvariant(), string.Empty).Trim();
        var rawLength = contentLength ?? string.Empty;
        var size = ContentLengthPattern.IsMatch(rawLength) ? rawLength + " bytes" : "unknown size";

        if (encoding.Length > 0 && !string.Equals(encoding, "identity", StringComparison.Ordinal))
        {
            return "[" + encoding + " " + size + "]";
        }

        if (mediaType.Length > 0
            && !(mediaType.Contains("json", StringComparison.Ordinal)
                || mediaType.StartsWith("text/", StringComparison.Ordinal)
                || mediaType.Contains("urlencoded", StringComparison.Ordinal)))
        {
            return "[" + mediaType + " " + size + "]";
        }

        return null;
    }

    /// <summary>Whether the request stream is worth tapping at all (JS parity).</summary>
    internal static bool IsCapturableRequest(string? contentType, string? contentEncoding)
    {
        var encoding = (contentEncoding ?? string.Empty).ToLowerInvariant();
        if (encoding.Length > 0 && !string.Equals(encoding, "identity", StringComparison.Ordinal))
        {
            return false;
        }

        var ct = (contentType ?? string.Empty).ToLowerInvariant();
        return ct.Contains("json", StringComparison.Ordinal)
            || ct.StartsWith("text/", StringComparison.Ordinal)
            || ct.Contains("urlencoded", StringComparison.Ordinal);
    }

    private static string Placeholder(string label, long total) =>
        string.Create(CultureInfo.InvariantCulture, $"[{label} {total} bytes]");

    /// <summary>JS <c>ct.split(";")[0] || fallback</c> — deliberately untrimmed.</summary>
    private static string FirstMediaType(string contentType, string fallback)
    {
        var cut = contentType.IndexOf(';');
        var head = cut < 0 ? contentType : contentType[..cut];
        return head.Length == 0 ? fallback : head;
    }
}

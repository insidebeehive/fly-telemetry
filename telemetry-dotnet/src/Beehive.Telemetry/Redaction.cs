using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Beehive.Telemetry;

/// <summary>
/// Single source of truth for "what must never reach a log line or a span".
/// </summary>
/// <remarks>
/// <para>
/// POLICY: logs are evidence. Business data — amounts, ids, card/account numbers,
/// bank transaction refs, paths — is logged VERBATIM. Redaction is reserved for
/// material that grants access:
/// </para>
/// <list type="bullet">
///   <item><description>passwords / OTPs / PINs</description></item>
///   <item><description>session ids and session/access tokens</description></item>
///   <item><description>API keys, secrets, private/access keys, credentials</description></item>
///   <item><description>webhook signatures (and their hash/salt keys)</description></item>
///   <item><description>Authorization / Cookie — never logged at all (the header ALLOWLIST below)</description></item>
/// </list>
/// <para>
/// There is deliberately no Luhn/PAN scrubbing: false positives redact the very
/// numeric refs these logs exist to keep.
/// </para>
/// <para>This is a direct port of the <c>@insidebeehive/telemetry</c> v0.2.x policy so
/// .NET and Node services redact identically.</para>
/// </remarks>
public static class Redaction
{
    /// <summary>Replacement written in place of a sensitive value.</summary>
    public const string Redacted = "[REDACTED]";

    /// <summary>Replacement written when the structure recursion floor is reached.</summary>
    public const string Truncated = "[TRUNCATED]";

    /// <summary>The body and query are untrusted input, so recursion needs a floor.</summary>
    private const int MaxDepth = 6;

    /// <summary>Untrusted input runs through these patterns; a runaway match must not pin a thread.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>
    /// Matched against key NAMES, by substring, case-insensitively, after normalisation.
    /// Entries are in normalized (alphanumeric-only) form — <see cref="NormaliseKey"/>
    /// strips underscores/dashes BEFORE matching, so <c>api_key</c>, <c>x-api-key</c>
    /// and <c>apiKey</c> all collapse to <c>apikey</c>.
    /// </summary>
    private static readonly Regex SensitiveKeyPattern = new(
        "(password|passwd|pwd|otp|mpin|session|token|secret|apikey|privatekey|accesskey|credential|authorization|cookie|signature|hashkey|saltkey)",
        Opts | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex NonAlphaNumeric = new("[^a-zA-Z0-9]", Opts, RegexTimeout);

    /// <summary>
    /// Sensitive only as a WHOLE word — as substrings they would over-match
    /// (<c>pin</c> would take <c>spinCount</c>, <c>sig</c> would take <c>design</c>).
    /// </summary>
    private static readonly HashSet<string> ExactSensitiveKeys = new(StringComparer.Ordinal) { "pin", "sig" };

    /// <summary>
    /// Headers worth logging, as an ALLOWLIST. This is what makes Cookie and Authorization
    /// structurally un-loggable: they are simply never picked. A denylist has to be extended
    /// every time an auth header is added; naming what is safe fails closed instead.
    /// </summary>
    public static readonly IReadOnlyList<string> SafeHeaders = new[]
    {
        "host", "content-type", "content-length", "user-agent", "referer", "origin",
        "accept-language", "x-forwarded-for", "userid", "operatorid", "traceparent",
    };

    /// <summary>Logged header values are capped at this many characters.</summary>
    private const int HeaderValueCap = 512;

    // --- scrubText regex passes (ported 1:1 from redact.js) -------------------
    // JS `$` without the /m flag anchors at end-of-input only; .NET `$` also matches
    // before a trailing newline, so `\z` is the faithful translation.
    private const string KeyDq = @"(?:\\.|[^""\\]){1,256}";   // double-quoted key: escapes ok, long keys ok
    private const string ValDq = @"(?:\\.|[^""\\])*";
    private const string KeySq = @"(?:\\.|[^'\\]){1,256}";    // single-quoted key
    private const string ValSq = @"(?:\\.|[^'\\])*";

    /// <summary>"key": "value" (escaped quotes allowed in the value).</summary>
    private static readonly Regex RxPairDq = new("\"(" + KeyDq + ")\"\\s*:\\s*\"" + ValDq + "\"", Opts, RegexTimeout);

    /// <summary>'key': 'value'.</summary>
    private static readonly Regex RxPairSq = new("'(" + KeySq + ")'\\s*:\\s*'" + ValSq + "'", Opts, RegexTimeout);

    /// <summary>Unterminated double-quoted value running to end of capped text
    /// (a trailing lone backslash from a mid-escape cut is allowed).</summary>
    private static readonly Regex RxOpenDq = new("\"(" + KeyDq + ")\"\\s*:\\s*\"" + ValDq + "\\\\?\\z", Opts, RegexTimeout);

    /// <summary>Unterminated single-quoted value running to end of capped text.</summary>
    private static readonly Regex RxOpenSq = new("'(" + KeySq + ")'\\s*:\\s*'" + ValSq + "\\\\?\\z", Opts, RegexTimeout);

    /// <summary>"key": [array...] (possibly truncated) — the whole array value goes.</summary>
    private static readonly Regex RxArray = new("\"(" + KeyDq + ")\"\\s*:\\s*\\[[^\\]]*(?:\\]|\\z)", Opts, RegexTimeout);

    /// <summary>"key": bareword/number/bool.</summary>
    private static readonly Regex RxBareword = new("\"(" + KeyDq + ")\"\\s*:\\s*([^\",{}\\[\\]\\s][^,}\\]]*)", Opts, RegexTimeout);

    /// <summary>urlencoded / query-ish pairs.</summary>
    private static readonly Regex RxUrlPair = new(@"(^|[&?])([^&=?\s]{1,256})=([^&\s]*)", Opts, RegexTimeout);

    /// <summary>
    /// Bare <c>key[:=]value</c> anywhere (plain-text shapes: "password: x", mid-text
    /// "token=x"). The lookbehind keeps the key unconsumed so adjacent pairs are each
    /// evaluated. Over-matching is the safe direction.
    /// </summary>
    private static readonly Regex RxBareKeyValue = new(
        @"(?<=(?:^|[\s{,;&(])([A-Za-z0-9_.\-]{1,256})\s*[:=]\s*)[^\s&,;:=""'{}\[\]]+", Opts, RegexTimeout);

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// Normalises a key for matching: non-alphanumerics are stripped, so <c>x-api-key</c>,
    /// <c>salt_key</c> and <c>saltKey</c> all collapse to the same form.
    /// </summary>
    /// <param name="key">Raw key name.</param>
    /// <returns>The alphanumeric-only form of <paramref name="key"/>.</returns>
    public static string NormaliseKey(string? key) => NonAlphaNumeric.Replace(key ?? string.Empty, string.Empty);

    /// <summary>
    /// THE test for "must this value be redacted?" — always use this, never the raw pattern.
    /// </summary>
    /// <param name="key">Key name, in any casing or separator style.</param>
    /// <returns><see langword="true"/> when the value under this key grants access and must be redacted.</returns>
    public static bool IsSensitiveKey(string? key)
    {
        try
        {
            var normalised = NormaliseKey(key);
            return ExactSensitiveKeys.Contains(normalised.ToLowerInvariant()) || SensitiveKeyPattern.IsMatch(normalised);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: an unanswerable key is treated as sensitive.
            return true;
        }
    }

    /// <summary>
    /// Best-effort scrub for RAW text that could not be parsed (truncated JSON, malformed
    /// JSON, unknown text bodies): redacts values of sensitive keys in JSON-ish and
    /// urlencoded-ish shapes. This is the backstop that keeps "cap then parse-fail" from
    /// leaking a credential verbatim — a token whose value is sliced by the byte cap is
    /// still caught by key.
    /// </summary>
    /// <param name="text">Raw, untrusted text.</param>
    /// <returns>The text with sensitive values replaced.</returns>
    public static string ScrubText(string? text)
    {
        try
        {
            // NUL bytes are never legitimate in textual payloads; NUL-interleaved text
            // (utf-16 bytes behind a lying utf-8 charset) would otherwise slip key names
            // past every regex below.
            var output = (text ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal);

            output = RxPairDq.Replace(output, DoubleQuotedReplacement);
            output = RxPairSq.Replace(output, SingleQuotedReplacement);
            output = RxOpenDq.Replace(output, DoubleQuotedReplacement);
            output = RxOpenSq.Replace(output, SingleQuotedReplacement);
            output = RxArray.Replace(output, DoubleQuotedReplacement);
            output = RxBareword.Replace(output, DoubleQuotedReplacement);
            output = RxUrlPair.Replace(output, static m =>
                IsSensitiveKey(DecodeSafe(m.Groups[2].Value))
                    ? m.Groups[1].Value + m.Groups[2].Value + "=" + Redacted
                    : m.Value);
            output = RxBareKeyValue.Replace(output, static m => IsSensitiveKey(m.Groups[1].Value) ? Redacted : m.Value);
            return output;
        }
        catch (RegexMatchTimeoutException)
        {
            // Unscrubbable text is dropped rather than logged raw.
            return Redacted;
        }
    }

    private static string DoubleQuotedReplacement(Match m) =>
        IsSensitiveKey(m.Groups[1].Value) ? "\"" + m.Groups[1].Value + "\":\"" + Redacted + "\"" : m.Value;

    private static string SingleQuotedReplacement(Match m) =>
        IsSensitiveKey(m.Groups[1].Value) ? "'" + m.Groups[1].Value + "':'" + Redacted + "'" : m.Value;

    /// <summary>
    /// Deep-copies <paramref name="node"/>, replacing any value whose KEY matches the
    /// sensitive test with <see cref="Redacted"/>. Structure is preserved so the shape of a
    /// payload stays debuggable while credentials do not survive. Values under non-sensitive
    /// keys pass through VERBATIM.
    /// </summary>
    /// <param name="node">Parsed JSON, or <see langword="null"/>.</param>
    /// <returns>A new, redacted tree.</returns>
    public static JsonNode? RedactJson(JsonNode? node) => RedactJsonCore(node, 0);

    private static JsonNode? RedactJsonCore(JsonNode? node, int depth)
    {
        if (depth >= MaxDepth)
        {
            return JsonValue.Create(Truncated);
        }

        switch (node)
        {
            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(RedactJsonCore(item, depth + 1));
                }

                return copy;
            }

            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var pair in obj)
                {
                    copy[pair.Key] = IsSensitiveKey(pair.Key) ? JsonValue.Create(Redacted) : RedactJsonCore(pair.Value, depth + 1);
                }

                return copy;
            }

            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// Convenience wrapper: parse <paramref name="json"/>, redact it, and render it back to
    /// one compact JSON string. Returns <see langword="null"/> when the text is not JSON.
    /// </summary>
    /// <param name="json">JSON text.</param>
    /// <returns>Redacted compact JSON, or <see langword="null"/> when unparseable.</returns>
    public static string? RedactJsonText(string? json)
    {
        try
        {
            return ToJsonString(RedactJson(JsonNode.Parse(json ?? string.Empty)));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static string ToJsonString(JsonNode? node) =>
        node is null ? "null" : node.ToJsonString(CompactJson);

    /// <summary>
    /// Wholesale query strip — for URLs where no param can be assumed safe (provider
    /// callbacks carry one-time tokens and signatures in the query).
    /// </summary>
    /// <param name="url">Any URL or path.</param>
    /// <returns>The URL with its entire query replaced.</returns>
    public static string StripQuery(string? url)
    {
        var value = url ?? string.Empty;
        var cut = value.IndexOf('?');
        return cut < 0 ? value : string.Concat(value.AsSpan(0, cut), "?", Redacted);
    }

    /// <summary>Per-key redaction of a bare query string ("a=1&amp;token=x" form).</summary>
    /// <param name="queryString">Query string, with or without a leading "?".</param>
    /// <returns>The query string with sensitive values replaced; <see cref="Redacted"/> when unparseable.</returns>
    public static string RedactQueryString(string? queryString)
    {
        try
        {
            return RedactFormEncoded(queryString);
        }
        catch (Exception)
        {
            // Unparseable query — safer to drop it than to log it raw.
            return Redacted;
        }
    }

    /// <summary>
    /// Per-key redaction of a URL's query string, keeping harmless params (page, filters,
    /// refs, amounts) legible. The path is logged verbatim — it is routing evidence.
    /// </summary>
    /// <param name="originalUrl">A URL or a path+query.</param>
    /// <returns>The URL with sensitive query values replaced.</returns>
    public static string RedactUrl(string? originalUrl)
    {
        var url = originalUrl ?? string.Empty;
        var cut = url.IndexOf('?');
        if (cut < 0)
        {
            return url;
        }

        // Mirrors JS `String(url).split("?")` destructuring: anything after a SECOND
        // "?" is not part of the query and is dropped.
        var rest = url[(cut + 1)..];
        var second = rest.IndexOf('?');
        var query = second < 0 ? rest : rest[..second];
        if (query.Length == 0)
        {
            return url;
        }

        return string.Concat(url.AsSpan(0, cut), "?", RedactQueryString(query));
    }

    /// <summary>
    /// Picks the loggable headers, in allowlist order, applying the referer URL redaction
    /// and the value length cap.
    /// </summary>
    /// <param name="lookup">Case-appropriate header getter; returns <see langword="null"/> when a header is absent.</param>
    /// <param name="allowed">Allowlist; defaults to <see cref="SafeHeaders"/>.</param>
    /// <returns>The picked headers, in allowlist order.</returns>
    public static List<KeyValuePair<string, string>> PickHeaders(Func<string, string?> lookup, IReadOnlyList<string>? allowed = null)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var result = new List<KeyValuePair<string, string>>();
        foreach (var name in allowed ?? SafeHeaders)
        {
            var value = lookup(name);
            if (value is null)
            {
                continue;
            }

            // referer is a URL — its query gets the same per-key redaction as the url
            // field (a session token in a referer query is still a session token).
            if (string.Equals(name, "referer", StringComparison.Ordinal))
            {
                value = RedactUrl(value);
            }

            // Cap logged header VALUES (an 8KB user-agent must not amplify every line).
            if (value.Length > HeaderValueCap)
            {
                var extra = (value.Length - HeaderValueCap).ToString(CultureInfo.InvariantCulture);
                value = string.Concat(value.AsSpan(0, HeaderValueCap), "…[+", extra, " chars]");
            }

            result.Add(new KeyValuePair<string, string>(name, value));
        }

        return result;
    }

    /// <summary>
    /// Picks the loggable headers from a dictionary keyed by lower-case header name.
    /// </summary>
    /// <param name="headers">Header dictionary.</param>
    /// <param name="allowed">Allowlist; defaults to <see cref="SafeHeaders"/>.</param>
    /// <returns>The picked headers, in allowlist order.</returns>
    public static List<KeyValuePair<string, string>> PickHeaders(IReadOnlyDictionary<string, string> headers, IReadOnlyList<string>? allowed = null)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return PickHeaders(name => headers.TryGetValue(name, out var value) ? value : null, allowed);
    }

    /// <summary>
    /// urlencoded body/query redaction with the JS <c>URLSearchParams</c> round-trip
    /// semantics: parse, replace sensitive values, re-serialise.
    /// </summary>
    internal static string RedactFormEncoded(string? input)
    {
        var text = input ?? string.Empty;
        if (text.StartsWith('?'))
        {
            text = text[1..];
        }

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var part in text.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? string.Empty : part[(eq + 1)..];
            pairs.Add(new KeyValuePair<string, string>(FormDecode(name), FormDecode(value)));
        }

        // URLSearchParams.set() replaces the FIRST occurrence and removes the rest, so
        // repeated sensitive keys collapse to a single redacted entry.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var pair in pairs)
        {
            var value = pair.Value;
            if (IsSensitiveKey(pair.Key))
            {
                if (!seen.Add(pair.Key))
                {
                    continue;
                }

                value = Redacted;
            }

            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            FormEncode(builder, pair.Key);
            builder.Append('=');
            FormEncode(builder, value);
        }

        return builder.ToString().Replace("%5BREDACTED%5D", Redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// Decodes an x-www-form-urlencoded body into a redacted key/value object, mirroring the
    /// JS <c>renderBody</c> urlencoded path: keys and values are form-decoded (<c>+</c> and
    /// <c>%XX</c>), a value whose key is sensitive becomes <see cref="Redacted"/>, and repeated
    /// keys collapse to an array so nothing is lost. The result reads identically to a JSON
    /// body with the same fields — spaces are spaces, no re-encoding.
    /// </summary>
    /// <param name="input">The raw urlencoded body, with or without a leading "?".</param>
    /// <returns>A JSON object of decoded, per-key-redacted fields.</returns>
    internal static JsonObject RedactFormToObject(string? input)
    {
        var text = input ?? string.Empty;
        if (text.StartsWith('?'))
        {
            text = text[1..];
        }

        // First-occurrence key order is preserved (URLSearchParams iteration order); values
        // accumulate per key so a repeated key can collapse to an array.
        var order = new List<string>();
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in text.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var eq = part.IndexOf('=');
            var name = FormDecode(eq < 0 ? part : part[..eq]);
            var value = FormDecode(eq < 0 ? string.Empty : part[(eq + 1)..]);

            if (!values.TryGetValue(name, out var list))
            {
                list = [];
                values[name] = list;
                order.Add(name);
            }

            list.Add(IsSensitiveKey(name) ? Redacted : value);
        }

        var obj = new JsonObject();
        foreach (var name in order)
        {
            var list = values[name];
            if (list.Count == 1)
            {
                obj[name] = JsonValue.Create(list[0]);
            }
            else
            {
                var array = new JsonArray();
                foreach (var value in list)
                {
                    array.Add(JsonValue.Create(value));
                }

                obj[name] = array;
            }
        }

        return obj;
    }

    private static string DecodeSafe(string value)
    {
        try
        {
            return PercentDecode(value);
        }
        catch (Exception)
        {
            return value;
        }
    }

    /// <summary>application/x-www-form-urlencoded parse: "+" is a space, then percent-decode.</summary>
    private static string FormDecode(string value) => PercentDecode(value.Replace('+', ' '));

    private static string PercentDecode(string value)
    {
        if (value.IndexOf('%') < 0)
        {
            return value;
        }

        var input = Encoding.UTF8.GetBytes(value);
        var output = new byte[input.Length];
        var length = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == (byte)'%' && i + 2 < input.Length && TryHex(input[i + 1], out var high) && TryHex(input[i + 2], out var low))
            {
                output[length++] = (byte)((high << 4) | low);
                i += 2;
            }
            else
            {
                output[length++] = input[i];
            }
        }

        return Encoding.UTF8.GetString(output, 0, length);
    }

    private static bool TryHex(byte c, out int value)
    {
        if (c >= '0' && c <= '9')
        {
            value = c - '0';
            return true;
        }

        if (c >= 'a' && c <= 'f')
        {
            value = c - 'a' + 10;
            return true;
        }

        if (c >= 'A' && c <= 'F')
        {
            value = c - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// application/x-www-form-urlencoded serialisation, matching the URL standard (and so
    /// <c>URLSearchParams.toString()</c>): space becomes "+", everything outside
    /// <c>[A-Za-z0-9*\-._]</c> is percent-encoded from its UTF-8 bytes, upper-case hex.
    /// </summary>
    private static void FormEncode(StringBuilder builder, string value)
    {
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b == 0x20)
            {
                builder.Append('+');
            }
            else if ((b >= (byte)'0' && b <= (byte)'9')
                || (b >= (byte)'A' && b <= (byte)'Z')
                || (b >= (byte)'a' && b <= (byte)'z')
                || b == (byte)'*' || b == (byte)'-' || b == (byte)'.' || b == (byte)'_')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(HexDigits[b >> 4]).Append(HexDigits[b & 0xF]);
            }
        }
    }

    private const string HexDigits = "0123456789ABCDEF";
}

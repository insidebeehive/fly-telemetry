using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Beehive.Telemetry;

/// <summary>
/// Writes structured lines straight to stdout, bypassing <c>ILogger</c> entirely.
/// </summary>
/// <remarks>
/// Two reasons this is not an <c>ILogger</c>: the http.access firehose must not be gated by
/// <c>LOG_LEVEL</c> (it is the 100% record), and routing it through the logging pipeline
/// whose console formatter this package also owns would risk formatter recursion. Crash
/// handlers use it too — mid-crash the logging pipeline may already be gone.
/// </remarks>
internal static class RawLog
{
    private static readonly JsonSerializerOptions LineJson = new()
    {
        // JSON.stringify escapes only what JSON requires; the relaxed encoder is the
        // System.Text.Json equivalent (no \uXXXX for '<', '&', '+' or non-ASCII).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly Regex TraceParent = new(
        "^[0-9a-f]{2}-([0-9a-f]{32})-([0-9a-f]{16})-([0-9a-f]{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    private const string AllZeroTraceId = "00000000000000000000000000000000";

    /// <summary>JS <c>new Date().toISOString()</c> shape, exactly.</summary>
    internal static string Timestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// Serialises and writes one line. A single <c>Write</c> call on the synchronised
    /// <see cref="Console.Out"/> keeps concurrent lines from interleaving.
    /// </summary>
    internal static void Write(JsonObject record)
    {
        try
        {
            // "\n", not Environment.NewLine: these are JSON *lines* for a log pipeline.
            Console.Out.Write(record.ToJsonString(LineJson) + "\n");
        }
        catch (Exception)
        {
            // A logging failure must never touch the request.
        }
    }

    /// <summary>
    /// Trace ids from the ambient <see cref="Activity"/>, in W3C form. Returns
    /// <see langword="false"/> when there is no recorded trace context.
    /// </summary>
    internal static bool TryActivityIds(out string traceId, out string spanId, out bool sampled)
    {
        traceId = string.Empty;
        spanId = string.Empty;
        sampled = false;
        try
        {
            var activity = Activity.Current;
            if (activity is null || activity.IdFormat != ActivityIdFormat.W3C)
            {
                return false;
            }

            var id = activity.TraceId.ToHexString();
            if (string.IsNullOrEmpty(id) || string.Equals(id, AllZeroTraceId, StringComparison.Ordinal))
            {
                return false;
            }

            traceId = id;
            spanId = activity.SpanId.ToHexString();
            sampled = (activity.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0;
            return true;
        }
        catch (Exception)
        {
            // Never let id capture break a request.
            return false;
        }
    }

    /// <summary>
    /// Fallback when tracing is off (or before a span exists): the caller's traceparent
    /// header still stitches the hops together.
    /// </summary>
    internal static bool TryHeaderIds(string? traceparent, out string traceId, out string spanId, out bool sampled)
    {
        traceId = string.Empty;
        spanId = string.Empty;
        sampled = false;
        if (string.IsNullOrEmpty(traceparent))
        {
            return false;
        }

        try
        {
            var match = TraceParent.Match(traceparent);
            if (!match.Success)
            {
                return false;
            }

            traceId = match.Groups[1].Value.ToLowerInvariant();
            spanId = match.Groups[2].Value.ToLowerInvariant();
            sampled = (Convert.ToInt32(match.Groups[3].Value, 16) & 1) == 1;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

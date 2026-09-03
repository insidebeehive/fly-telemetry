using System.Globalization;

namespace Beehive.Telemetry.Http;

/// <summary>When the payload policy fires.</summary>
internal enum PayloadMode
{
    /// <summary>Every line carries redacted headers + capped bodies.</summary>
    Always,

    /// <summary>Only errors (status &gt;= 400) and slow requests.</summary>
    Errors,

    /// <summary>Never — access lines only.</summary>
    Off,
}

/// <summary>How captured bodies land on the line.</summary>
internal enum BodyMode
{
    /// <summary>Redacted then kept as ONE JSON-encoded string field (default).</summary>
    String,

    /// <summary>Parsed + redacted bodies land as nested fields.</summary>
    Object,
}

/// <summary>
/// Per-app HTTP logging policy, read from the environment exactly once at startup so a
/// deployment can retune it without a code change.
/// </summary>
internal sealed class HttpLogOptions
{
    internal const string DefaultIgnorePaths = "/,/health,/healthz,/favicon.ico";

    private HttpLogOptions(
        bool enabled,
        PayloadMode payloadMode,
        double slowMs,
        int bodyMax,
        BodyMode bodyMode,
        string[] payloadRoutes,
        string[] ignorePaths,
        string service)
    {
        Enabled = enabled;
        PayloadMode = payloadMode;
        SlowMs = slowMs;
        BodyMax = bodyMax;
        BodyMode = bodyMode;
        PayloadRoutes = payloadRoutes;
        IgnorePaths = ignorePaths;
        Service = service;
    }

    internal bool Enabled { get; }

    internal PayloadMode PayloadMode { get; }

    internal double SlowMs { get; }

    internal int BodyMax { get; }

    internal BodyMode BodyMode { get; }

    internal string[] PayloadRoutes { get; }

    internal string[] IgnorePaths { get; }

    internal string Service { get; }

    /// <summary>Bodies are never captured for these methods (JS parity).</summary>
    internal static bool IsBodyless(string? method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase);

    internal static HttpLogOptions FromEnvironment(string service)
    {
        var enabled = !string.Equals(TelemetryEnv.Choice("HTTP_LOG", "on", "on", "off"), "off", StringComparison.Ordinal);

        var payload = TelemetryEnv.Choice("HTTP_LOG_PAYLOAD", "always", "always", "errors", "off") switch
        {
            "errors" => PayloadMode.Errors,
            "off" => PayloadMode.Off,
            _ => PayloadMode.Always,
        };

        var slowMs = TelemetryEnv.Number("HTTP_LOG_SLOW_MS", 1000, min: 0);

        // JS validates "> 0"; the cap is also clamped so a huge value cannot be used to
        // blow up the process through one oversized allocation per request.
        var bodyMaxRaw = TelemetryEnv.Number("HTTP_LOG_BODY_MAX", 4096, min: 1, max: 8 * 1024 * 1024);
        var bodyMax = (int)Math.Min(bodyMaxRaw, 8 * 1024 * 1024);

        var bodyMode = string.Equals(TelemetryEnv.Choice("HTTP_LOG_BODY_MODE", "string", "string", "object"), "object", StringComparison.Ordinal)
            ? BodyMode.Object
            : BodyMode.String;

        return new HttpLogOptions(
            enabled,
            payload,
            slowMs,
            bodyMax,
            bodyMode,
            TelemetryEnv.List("HTTP_LOG_PAYLOAD_ROUTES", string.Empty),
            TelemetryEnv.List("HTTP_LOG_IGNORE_PATHS", DefaultIgnorePaths),
            service);
    }

    internal bool IsIgnored(string path) => TelemetryEnv.IsIgnoredPath(path, IgnorePaths);

    /// <summary>Whether headers + bodies attach to this particular line.</summary>
    internal bool ShouldEnrich(string path, int status, double durationMs)
    {
        if (PayloadMode == PayloadMode.Always)
        {
            return true;
        }

        if (PayloadMode == PayloadMode.Errors && (status >= 400 || durationMs >= SlowMs))
        {
            return true;
        }

        foreach (var prefix in PayloadRoutes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"http logger enabled (payload={PayloadMode.ToString().ToLowerInvariant()}, body_max={BodyMax}, body_mode={BodyMode.ToString().ToLowerInvariant()})");
}

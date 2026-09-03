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

    /// <summary>
    /// Front-end static assets are high-volume, near-zero-signal noise (a frontend can serve
    /// more asset requests than real ones), so they are skipped by file EXTENSION by default.
    /// Deliberately only front-end static types: NOT pdf/csv/xlsx/zip, which are business
    /// downloads worth logging.
    /// </summary>
    internal const string DefaultIgnoreExtensions = "js,mjs,cjs,css,map,ico,png,jpg,jpeg,gif,svg,webp,avif,woff,woff2,ttf,eot";

    private HttpLogOptions(
        bool enabled,
        PayloadMode payloadMode,
        double slowMs,
        int bodyMax,
        BodyMode bodyMode,
        string[] payloadRoutes,
        string[] ignorePaths,
        IReadOnlySet<string> ignoreExtensions,
        string service)
    {
        Enabled = enabled;
        PayloadMode = payloadMode;
        SlowMs = slowMs;
        BodyMax = bodyMax;
        BodyMode = bodyMode;
        PayloadRoutes = payloadRoutes;
        IgnorePaths = ignorePaths;
        IgnoreExtensions = ignoreExtensions;
        Service = service;
    }

    internal bool Enabled { get; }

    internal PayloadMode PayloadMode { get; }

    internal double SlowMs { get; }

    internal int BodyMax { get; }

    internal BodyMode BodyMode { get; }

    internal string[] PayloadRoutes { get; }

    internal string[] IgnorePaths { get; }

    /// <summary>Lower-cased file extensions (no leading dot) whose asset requests emit no line.</summary>
    internal IReadOnlySet<string> IgnoreExtensions { get; }

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
            ParseIgnoreExtensions(),
            service);
    }

    /// <summary>
    /// Parses <c>HTTP_LOG_IGNORE_EXTENSIONS</c>: a case-insensitive comma list, leading dots
    /// stripped, defaulting to the front-end static set. <c>off</c>/<c>none</c> yields an empty
    /// set (assets are logged too).
    /// </summary>
    private static IReadOnlySet<string> ParseIgnoreExtensions()
    {
        var raw = TelemetryEnv.Get("HTTP_LOG_IGNORE_EXTENSIONS", DefaultIgnoreExtensions).ToLowerInvariant().Trim();
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.Equals(raw, "off", StringComparison.Ordinal) || string.Equals(raw, "none", StringComparison.Ordinal))
        {
            return set;
        }

        foreach (var part in raw.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith('.'))
            {
                trimmed = trimmed[1..];
            }

            if (trimmed.Length > 0)
            {
                set.Add(trimmed);
            }
        }

        return set;
    }

    /// <summary>
    /// Skipped entirely when the path is on the ignore list, or its last segment ends in an
    /// ignored file extension — same effect either way (no <c>http.access</c> line).
    /// </summary>
    internal bool IsIgnored(string path)
    {
        if (TelemetryEnv.IsIgnoredPath(path, IgnorePaths))
        {
            return true;
        }

        if (IgnoreExtensions.Count > 0)
        {
            var slash = path.LastIndexOf('/');
            var dot = path.LastIndexOf('.');
            if (dot > slash && dot < path.Length - 1
                && IgnoreExtensions.Contains(path[(dot + 1)..].ToLowerInvariant()))
            {
                return true;
            }
        }

        return false;
    }

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

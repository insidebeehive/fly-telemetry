using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Beehive.Telemetry.Tracing;

/// <summary>
/// OpenTelemetry bootstrap — exports TRACES ONLY to an OTLP collector.
/// </summary>
/// <remarks>
/// <para>
/// The OTLP endpoint is the master switch and is deliberately never defaulted: where traces
/// go is deployment config. No endpoint means no tracing and no SDK objects at all, which is
/// what keeps local dev and CI clean.
/// </para>
/// <para>
/// Everything else is written into the STANDARD <c>OTEL_*</c> environment variables when
/// unset, so overriding any of them behaves exactly like stock OpenTelemetry.
/// </para>
/// </remarks>
internal static class TracingSetup
{
    private const double DefaultSampleRatio = 0.1;

    internal const string DefaultIgnorePaths = "/,/health,/healthz,/favicon.ico";

    /// <summary>
    /// Configures tracing on the builder, or reports why it stayed off. Returns
    /// <see langword="true"/> when the SDK was wired up.
    /// </summary>
    internal static bool Configure(IHostApplicationBuilder builder, string serviceName)
    {
        if (IsTruthy(TelemetryEnv.Raw("OTEL_SDK_DISABLED")))
        {
            TelemetryEnv.Info("tracing disabled via OTEL_SDK_DISABLED");
            return false;
        }

        var endpoint = TelemetryEnv.Raw("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT") ?? TelemetryEnv.Raw("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (endpoint is null)
        {
            TelemetryEnv.Info("tracing disabled (set OTEL_EXPORTER_OTLP_ENDPOINT to enable)");
            return false;
        }

        // Defaults are written into the STANDARD OTEL_* variables, so overriding any of
        // them behaves exactly like stock OpenTelemetry. Only keys the app left unset are
        // touched — see SetDefault.
        var defaults = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Collectors in this stack speak protobuf over HTTP; the SDK's own default is gRPC.
        SetDefault(defaults, "OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");

        // service.name has its own standard variable; setting it (when the app has not)
        // makes the resource agree with the `service` field on every log line.
        SetDefault(defaults, "OTEL_SERVICE_NAME", serviceName);

        // The instrumentation's own default replaces EVERY query value with "Redacted",
        // which contradicts this package's policy: paths and harmless params (page, filters,
        // refs, amounts) are evidence and must stay legible, while session/token/key params
        // must not. ScrubbingSpanProcessor applies exactly that per-key rule, so spans and
        // http.access lines agree on one url field instead of two different ones. An app that
        // prefers the blanket redaction sets these to "false".
        SetDefault(defaults, "OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "true");
        SetDefault(defaults, "OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION", "true");

        ComposeResourceAttributes(defaults, serviceName);

        // Setting the environment variable is not enough on .NET: the host snapshots the
        // environment into IConfiguration when the builder is created, and that snapshot —
        // not the live environment — is what the OTel options binder reads. Adding the same
        // values as a configuration source keeps both paths in agreement. It is appended
        // last on purpose: every value here was either absent from the app's configuration
        // or composed from it, so nothing the app set is overwritten.
        if (defaults.Count > 0)
        {
            builder.Configuration.AddInMemoryCollection(defaults);
        }

        var ratio = ResolveSampleRatio();
        var ignored = TelemetryEnv.List("OTEL_IGNORE_PATHS", DefaultIgnorePaths);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: serviceName,
                autoGenerateServiceInstanceId: false))
            .WithTracing(tracing => tracing
                /*
                 * Head sampling, parent-based: the entry service rolls the dice once per
                 * request and every downstream service follows that decision, so sampled
                 * requests keep their COMPLETE journey. The default is 0.1 — spans are the
                 * code-level 10%; the 100% "which request went where" record is the
                 * http.access line's job.
                 */
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)))
                .AddAspNetCoreInstrumentation(instrumentation =>
                {
                    // Paths that would otherwise dominate trace volume while telling us
                    // nothing: health checks and the root liveness probe.
                    instrumentation.Filter = context => !IsIgnoredRequest(context, ignored);

                    // No header capture. Auth headers live there and must never reach the
                    // trace store; the scrubbing processor below is the backstop.
                })
                .AddHttpClientInstrumentation()
                .AddProcessor(new ScrubbingSpanProcessor())
                .AddOtlpExporter());

        builder.Services.AddSingleton<IHostedService, TracerFlushHostedService>();

        TelemetryEnv.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"tracing enabled -> {endpoint} (sampler={ratio})"));

        return true;
    }

    private static bool IsIgnoredRequest(HttpContext context, string[] ignored)
    {
        try
        {
            var path = (context.Request.PathBase.Value ?? string.Empty) + (context.Request.Path.Value ?? string.Empty);
            return TelemetryEnv.IsIgnoredPath(path.Length == 0 ? "/" : path, ignored);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static double ResolveSampleRatio()
    {
        var raw = Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER_ARG");
        if (raw is null)
        {
            return DefaultSampleRatio;
        }

        if (raw.Trim().Length > 0
            && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed)
            && parsed >= 0 && parsed <= 1)
        {
            return parsed;
        }

        TelemetryEnv.Warn($"invalid OTEL_TRACES_SAMPLER_ARG \"{raw}\" — using 0.1 (valid: 0..1)");
        return DefaultSampleRatio;
    }

    /// <summary>
    /// Writes a standard OTel variable only when the app left it unset, recording it so the
    /// same value can be mirrored into configuration.
    /// </summary>
    private static void SetDefault(Dictionary<string, string?> defaults, string name, string value)
    {
        if (TelemetryEnv.Raw(name) is not null)
        {
            return;
        }

        Environment.SetEnvironmentVariable(name, value);
        defaults[name] = value;
    }

    /// <summary>
    /// Composes the platform-derived resource attributes INTO <c>OTEL_RESOURCE_ATTRIBUTES</c>
    /// (only keys the app did not set), then lets the standard env detector materialise them —
    /// so a per-app override wins over every default here, exactly like stock OpenTelemetry.
    /// </summary>
    private static void ComposeResourceAttributes(Dictionary<string, string?> defaults, string serviceName)
    {
        var existing = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES") ?? string.Empty;
        var provided = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in existing.Split(','))
        {
            var key = pair.Split('=')[0].Trim();
            if (key.Length > 0)
            {
                provided.Add(key);
            }
        }

        var appName = TelemetryEnv.Raw("FLY_APP_NAME");
        var region = TelemetryEnv.Raw("FLY_REGION");
        var machineId = TelemetryEnv.Raw("FLY_MACHINE_ID");
        var imageRef = TelemetryEnv.Raw("FLY_IMAGE_REF");

        var platformAttributes = new List<KeyValuePair<string, string>>(5);
        if (appName is not null)
        {
            platformAttributes.Add(new KeyValuePair<string, string>("cloud.provider", "fly_io"));
        }

        platformAttributes.Add(new KeyValuePair<string, string>("cloud.region", region ?? "auto"));
        platformAttributes.Add(new KeyValuePair<string, string>("service.instance.id", machineId ?? "NA"));
        platformAttributes.Add(new KeyValuePair<string, string>("service.version", TelemetryEnv.Raw("OTEL_SERVICE_VERSION") ?? imageRef ?? "NA"));
        platformAttributes.Add(new KeyValuePair<string, string>("deployment.environment.name", EnvironmentName()));

        var additions = new StringBuilder();
        foreach (var pair in platformAttributes)
        {
            if (provided.Contains(pair.Key))
            {
                continue;
            }

            if (additions.Length > 0)
            {
                additions.Append(',');
            }

            additions.Append(pair.Key).Append('=').Append(Sanitise(pair.Value));
        }

        if (additions.Length > 0)
        {
            var composed = existing.Length > 0 ? existing + "," + additions : additions.ToString();
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", composed);
            defaults["OTEL_RESOURCE_ATTRIBUTES"] = composed;
        }

        WarnAboutMissingPlatformVars(serviceName, provided, appName, region, machineId, imageRef);
    }

    /// <summary>
    /// Missing platform vars are warned about ONCE, at registration — never per request. The
    /// warning ends with ready-to-paste lines carrying every missing key, so a reader sees in
    /// one place exactly what to set. A variable the app already supplied via an override is
    /// not warned about: nothing is missing from the telemetry then.
    /// </summary>
    private static void WarnAboutMissingPlatformVars(
        string serviceName,
        HashSet<string> provided,
        string? appName,
        string? region,
        string? machineId,
        string? imageRef)
    {
        var missing = new List<string>();
        var attributePairs = new List<string>();
        var needsServiceName = false;

        if (appName is null && TelemetryEnv.Raw("OTEL_SERVICE_NAME") is null)
        {
            missing.Add($"  - FLY_APP_NAME not set -> service.name falls back to \"{serviceName}\"");
            needsServiceName = true;
        }

        if (region is null && !provided.Contains("cloud.region"))
        {
            missing.Add("  - FLY_REGION not set -> cloud.region falls back to \"auto\"");
            attributePairs.Add("cloud.region=<region>");
        }

        if (machineId is null && !provided.Contains("service.instance.id"))
        {
            missing.Add("  - FLY_MACHINE_ID not set -> service.instance.id falls back to \"NA\"");
            attributePairs.Add("service.instance.id=<machine-or-host-id>");
        }

        if (imageRef is null && TelemetryEnv.Raw("OTEL_SERVICE_VERSION") is null && !provided.Contains("service.version"))
        {
            missing.Add("  - FLY_IMAGE_REF not set -> service.version falls back to \"NA\"");
            attributePairs.Add("service.version=<version-or-image>");
        }

        if (missing.Count == 0)
        {
            return;
        }

        var fixes = new List<string>();
        if (needsServiceName)
        {
            fixes.Add("    OTEL_SERVICE_NAME=<service-name>");
        }

        if (attributePairs.Count > 0)
        {
            fixes.Add("    OTEL_RESOURCE_ATTRIBUTES=" + string.Join(",", attributePairs));
        }

        TelemetryEnv.Warn(
            "platform env not available (warned once, at registration). Fly sets these automatically at runtime; elsewhere provide them explicitly:\n"
            + string.Join("\n", missing)
            + "\n  To set them, add to the environment (OTEL_RESOURCE_ATTRIBUTES is ONE variable, comma-separated — keep keys you already set and replace the <placeholders>):\n"
            + string.Join("\n", fixes));
    }

    private static string EnvironmentName()
    {
        var name = TelemetryEnv.Raw("ASPNETCORE_ENVIRONMENT") ?? TelemetryEnv.Raw("DOTNET_ENVIRONMENT") ?? "development";
        return name.ToLowerInvariant();
    }

    /// <summary>Keeps a stray separator in a platform value from corrupting the composed variable.</summary>
    private static string Sanitise(string value) => value.Replace(',', '_').Replace('=', '_');

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "1", StringComparison.Ordinal);
}

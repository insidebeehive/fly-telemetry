using Beehive.Telemetry.Http;
using Beehive.Telemetry.Logging;
using Beehive.Telemetry.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Beehive.Telemetry;

/// <summary>
/// The whole integration surface: one call on the host builder.
/// </summary>
/// <remarks>
/// <example>
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// builder.AddBeehiveTelemetry();
/// </code>
/// </example>
/// <para>That single call wires up:</para>
/// <list type="number">
///   <item><description>OpenTelemetry tracing (off until an OTLP endpoint is configured);</description></item>
///   <item><description>the <c>http.access</c> middleware, placed FIRST in the pipeline automatically
///   via an <c>IStartupFilter</c> — the application never adds a <c>Use…</c> call;</description></item>
///   <item><description>console logging (JSON in production, coloured locally), audit support and
///   crash handlers.</description></item>
/// </list>
/// <para>
/// Calling it twice is a no-op, and every part is individually fail-safe: a telemetry
/// failure is reported and swallowed, never propagated. Observability must not be the
/// reason a service fails to boot.
/// </para>
/// </remarks>
public static class BeehiveTelemetryExtensions
{
    /// <summary>
    /// Adds Beehive telemetry — tracing, HTTP access logging and structured app logging — to
    /// a web application builder.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static WebApplicationBuilder AddBeehiveTelemetry(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddBeehiveTelemetry((IHostApplicationBuilder)builder);
        return builder;
    }

    /// <summary>
    /// Adds Beehive telemetry — tracing, HTTP access logging and structured app logging — to
    /// any host application builder.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IHostApplicationBuilder AddBeehiveTelemetry(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Idempotent per builder: an app that both calls this and inherits it from a shared
        // bootstrap is still instrumented exactly once.
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(TelemetryMarker)))
        {
            return builder;
        }

        builder.Services.AddSingleton<TelemetryMarker>();

        var service = ServiceName.Resolve();

        try
        {
            TracingSetup.Configure(builder, service);
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("tracing init failed, continuing without it", error);
        }

        try
        {
            ConfigureHttpLogging(builder, service);
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("http logger init failed, continuing without it", error);
        }

        try
        {
            LoggingSetup.Configure(builder.Logging, builder.Environment.EnvironmentName);
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("app logger init failed, continuing without it", error);
        }

        try
        {
            // Registered at activation, so a startup crash before the first log call is
            // still captured.
            CrashHandlers.Install();
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("crash handler init failed, continuing without it", error);
        }

        return builder;
    }

    private static void ConfigureHttpLogging(IHostApplicationBuilder builder, string service)
    {
        var options = HttpLogOptions.FromEnvironment(service);
        if (!options.Enabled)
        {
            TelemetryEnv.Info("http logger disabled via HTTP_LOG=off");
            return;
        }

        builder.Services.AddSingleton(options);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, HttpAccessLogStartupFilter>());
        TelemetryEnv.Info(options.Describe());
    }

    /// <summary>Marker service whose presence makes a second <c>AddBeehiveTelemetry</c> a no-op.</summary>
    private sealed class TelemetryMarker
    {
    }
}

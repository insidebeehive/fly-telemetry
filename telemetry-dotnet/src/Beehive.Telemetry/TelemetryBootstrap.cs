using Beehive.Telemetry.Http;
using Beehive.Telemetry.Logging;
using Beehive.Telemetry.Tracing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Beehive.Telemetry;

/// <summary>
/// The single wiring core, shared by the <c>AddBeehiveTelemetry</c> extension and the
/// zero-code <see cref="TelemetryHostingStartup"/>.
/// </summary>
/// <remarks>
/// <para>
/// It wires the same three concerns either path activates — OpenTelemetry tracing, the
/// <c>http.access</c> middleware (via an <see cref="IStartupFilter"/>), and structured app
/// logging with crash handlers — so neither entry point duplicates the setup.
/// </para>
/// <para>
/// The pieces are passed individually — an <see cref="IServiceCollection"/>, an
/// <see cref="IConfigurationBuilder"/> for the mirrored <c>OTEL_*</c> defaults, and an
/// <see cref="ILoggingBuilder"/> — rather than a host builder, because the two activation
/// paths obtain them from different places: the extension from a single
/// <c>IHostApplicationBuilder</c>, the hosting startup from <c>IWebHostBuilder</c>'s separate
/// configuration and service callbacks.
/// </para>
/// </remarks>
internal static class TelemetryBootstrap
{
    /// <summary>
    /// Applies the whole integration once. Guarded by <see cref="TelemetryMarker"/>, so
    /// whichever activation path runs first wins and any later one is a no-op — an app that
    /// inherits the hosting-startup wiring AND also calls <c>AddBeehiveTelemetry()</c> is still
    /// instrumented exactly once. Every concern is individually fail-safe: a telemetry failure
    /// is reported and swallowed, never propagated.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">
    /// The configuration the composed <c>OTEL_*</c> defaults are mirrored into. The OTLP
    /// options binder reads the configuration snapshot, not live environment variables, so the
    /// defaults have to land here as well as in the environment.
    /// </param>
    /// <param name="logging">The logging builder the console formatter is registered on.</param>
    /// <param name="environmentName">The host environment name, e.g. <c>Production</c>.</param>
    internal static void Apply(
        IServiceCollection services,
        IConfigurationBuilder configuration,
        ILoggingBuilder logging,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logging);

        // Idempotent across BOTH activation paths: the marker is a DI singleton, so whichever
        // path reaches the collection first reserves it and any later one returns here.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(TelemetryMarker)))
        {
            return;
        }

        services.AddSingleton<TelemetryMarker>();

        var service = ServiceName.Resolve();

        try
        {
            TracingSetup.Configure(services, configuration, service);
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("tracing init failed, continuing without it", error);
        }

        try
        {
            ConfigureHttpLogging(services, service);
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("http logger init failed, continuing without it", error);
        }

        try
        {
            LoggingSetup.Configure(logging, environmentName);
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
    }

    private static void ConfigureHttpLogging(IServiceCollection services, string service)
    {
        var options = HttpLogOptions.FromEnvironment(service);
        if (!options.Enabled)
        {
            TelemetryEnv.Info("http logger disabled via HTTP_LOG=off");
            return;
        }

        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, HttpAccessLogStartupFilter>());
        TelemetryEnv.Info(options.Describe());
    }

    /// <summary>Marker service whose presence makes any later activation a no-op.</summary>
    internal sealed class TelemetryMarker
    {
    }
}

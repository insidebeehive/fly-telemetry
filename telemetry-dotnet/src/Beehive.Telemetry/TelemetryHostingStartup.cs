using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: HostingStartup(typeof(Beehive.Telemetry.TelemetryHostingStartup))]

namespace Beehive.Telemetry;

/// <summary>
/// Zero-code activation — the .NET analog of the npm package's
/// <c>NODE_OPTIONS="--import @insidebeehive/telemetry/register"</c>.
/// </summary>
/// <remarks>
/// <para>
/// This runs ONLY when the assembly is listed in the standard
/// <c>ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=Beehive.Telemetry</c> environment variable — the
/// <see cref="HostingStartupAttribute"/> above is inert otherwise. Referencing the package
/// without that variable does nothing, so there is no surprise instrumentation; an image you
/// cannot edit becomes fully instrumented purely by setting the variable.
/// </para>
/// <para>
/// It wires the SAME three concerns as <c>builder.AddBeehiveTelemetry()</c> through the shared
/// <see cref="TelemetryBootstrap"/> core, and shares that core's <c>TelemetryMarker</c> guard:
/// an app that sets the variable AND also calls <c>AddBeehiveTelemetry()</c> is instrumented
/// exactly once.
/// </para>
/// </remarks>
public sealed class TelemetryHostingStartup : IHostingStartup
{
    /// <summary>
    /// Registers the telemetry wiring on the web host. Fail-safe: any failure is reported and
    /// swallowed so observability never stops the app booting.
    /// </summary>
    /// <param name="builder">The web host builder supplied by the hosting-startup mechanism.</param>
    public void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        try
        {
            // The OTLP options binder reads the IConfiguration snapshot, not live environment
            // variables. This private manager is chained into the app configuration NOW (while
            // empty) as a LIVE source; TracingSetup fills it during ConfigureServices below,
            // and the chained provider reads through to it, so the mirrored OTEL_* defaults
            // reach the binder even though they are written after the app configuration was
            // first built. It is the hosting-startup equivalent of the extension appending an
            // in-memory source to builder.Configuration.
            var mirrored = new ConfigurationManager();
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddConfiguration(mirrored));

            builder.ConfigureServices((context, services) =>
                // AddLogging invokes its delegate synchronously, so the IServiceCollection, the
                // mirrored configuration and the ILoggingBuilder are all in hand for a single
                // shared-core call — the same wiring the extension performs.
                services.AddLogging(logging =>
                    TelemetryBootstrap.Apply(services, mirrored, logging, context.HostingEnvironment.EnvironmentName)));
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("hosting-startup activation failed, continuing without it", error);
        }
    }
}

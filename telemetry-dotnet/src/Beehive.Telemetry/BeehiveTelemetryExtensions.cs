using Microsoft.AspNetCore.Builder;
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

        // The whole integration lives in the shared core so this call and the zero-code
        // hosting startup wire exactly the same three concerns; the core's marker guard keeps
        // a second activation (from either path) a no-op.
        TelemetryBootstrap.Apply(
            builder.Services,
            builder.Configuration,
            builder.Logging,
            builder.Environment.EnvironmentName);

        return builder;
    }
}

using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;

namespace Beehive.Telemetry.Tracing;

/// <summary>
/// Flushes the last span batch on shutdown rather than losing the spans for whatever was in
/// flight — which, during a bad deploy, is exactly the window worth having traces for.
/// </summary>
/// <remarks>
/// The 3s budget is deliberate: platforms send SIGKILL after a short grace period, and an
/// unreachable collector must not push shutdown past it.
/// </remarks>
internal sealed class TracerFlushHostedService : IHostedService
{
    private const int FlushBudgetMs = 3000;

    private readonly IHostApplicationLifetime lifetime;
    private readonly TracerProvider? tracerProvider;
    private int flushed;

    public TracerFlushHostedService(IHostApplicationLifetime lifetime, TracerProvider? tracerProvider = null)
    {
        this.lifetime = lifetime;
        this.tracerProvider = tracerProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            lifetime.ApplicationStopping.Register(Flush);
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("could not register the trace flush hook, continuing without it", error);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Belt and braces: if ApplicationStopping never fired (an abrupt host stop), this is
        // still the last chance to get the batch out.
        Flush();
        return Task.CompletedTask;
    }

    private void Flush()
    {
        if (Interlocked.Exchange(ref flushed, 1) == 1)
        {
            return;
        }

        try
        {
            tracerProvider?.ForceFlush(FlushBudgetMs);
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("trace flush failed on shutdown", error);
        }
    }
}

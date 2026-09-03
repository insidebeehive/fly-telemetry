using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Beehive.Telemetry.Tests;

/// <summary>
/// The one-line integration itself: what a single <c>AddBeehiveTelemetry()</c> registers,
/// and what a second one does not.
/// </summary>
public class IntegrationTests : IDisposable
{
    private static readonly string[] Owned =
    [
        "HTTP_LOG", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
        "OTEL_SDK_DISABLED", "LOG_LEVEL", "LOG_FORMAT",
    ];

    private readonly Dictionary<string, string?> saved = [];

    public IntegrationTests()
    {
        foreach (var name in Owned)
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var pair in saved)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        GC.SuppressFinalize(this);
    }

    private static WebApplicationBuilder NewBuilder() =>
        WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });

    private static int StartupFilterCount(IServiceCollection services) =>
        services.Count(descriptor => descriptor.ImplementationType?.Name == "HttpAccessLogStartupFilter");

    [Fact]
    public void OneCallInstallsTheAccessLogMiddlewareWithoutAnyUseCall()
    {
        var builder = NewBuilder();

        builder.AddBeehiveTelemetry();

        Assert.Equal(1, StartupFilterCount(builder.Services));
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IStartupFilter));
    }

    [Fact]
    public void ASecondCallIsANoOp()
    {
        var builder = NewBuilder();

        builder.AddBeehiveTelemetry();
        var afterFirst = builder.Services.Count;
        builder.AddBeehiveTelemetry();
        builder.AddBeehiveTelemetry();

        Assert.Equal(afterFirst, builder.Services.Count);
        Assert.Equal(1, StartupFilterCount(builder.Services));
    }

    [Fact]
    public void HttpLogOffSkipsTheMiddlewareEntirely()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG", "off");
        var builder = NewBuilder();

        builder.AddBeehiveTelemetry();

        Assert.Equal(0, StartupFilterCount(builder.Services));
    }

    [Fact]
    public void TracingStaysOffAndConstructsNoSdkObjectsWithoutAnEndpoint()
    {
        var builder = NewBuilder();

        builder.AddBeehiveTelemetry();

        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(OpenTelemetry.Trace.TracerProvider));
    }

    [Fact]
    public void TracingIsWiredUpWhenAnEndpointIsConfigured()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4318");
        var builder = NewBuilder();

        builder.AddBeehiveTelemetry();

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(OpenTelemetry.Trace.TracerProvider));

        // The protocol default has to reach IConfiguration, not just the environment: the
        // host snapshotted the environment before this call ran.
        Assert.Equal("http/protobuf", builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"]);
    }

    [Fact]
    public void OtelSdkDisabledStopsTracingButNotLogging()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4318");
        Environment.SetEnvironmentVariable("OTEL_SDK_DISABLED", "true");
        var builder = NewBuilder();

        builder.AddBeehiveTelemetry();

        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(OpenTelemetry.Trace.TracerProvider));
        Assert.Equal(1, StartupFilterCount(builder.Services));
    }

    [Fact]
    public void AuditSurvivesTheStrictestLogLevel()
    {
        Environment.SetEnvironmentVariable("LOG_LEVEL", "error");
        var builder = NewBuilder();
        builder.AddBeehiveTelemetry();

        using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void TheAppStillBuildsAndRunsItsPipeline()
    {
        var builder = NewBuilder();
        builder.AddBeehiveTelemetry();

        using var app = builder.Build();
        app.MapGet("/hello", () => "hi");

        // Building the request pipeline is what runs every IStartupFilter; if the middleware
        // could not be added, this is where it would surface.
        Assert.NotNull(((IApplicationBuilder)app).Build());
    }
}

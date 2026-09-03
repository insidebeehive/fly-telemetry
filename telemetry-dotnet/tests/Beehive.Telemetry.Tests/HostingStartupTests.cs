using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Beehive.Telemetry.Tests;

/// <summary>
/// The zero-code activation path (<c>ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=Beehive.Telemetry</c>):
/// the type contract, that it wires the same services as the extension, and that the two paths
/// together instrument exactly once.
/// </summary>
public class HostingStartupTests : IDisposable
{
    private static readonly string[] Owned =
    [
        "HTTP_LOG", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
        "OTEL_SDK_DISABLED", "LOG_LEVEL", "LOG_FORMAT",
    ];

    private readonly Dictionary<string, string?> saved = [];

    public HostingStartupTests()
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

    private static int MarkerCount(IServiceCollection services) =>
        services.Count(descriptor => descriptor.ServiceType == typeof(TelemetryBootstrap.TelemetryMarker));

    private static int StartupFilterCount(IServiceCollection services) =>
        services.Count(descriptor => descriptor.ImplementationType?.Name == "HttpAccessLogStartupFilter");

    /// <summary>
    /// Drives <see cref="TelemetryHostingStartup"/> exactly the way the runtime does: it calls
    /// <c>Configure</c>, then runs the callbacks it registered — app configuration first, then
    /// services — against the supplied collection.
    /// </summary>
    private static void ActivateHostingStartup(IServiceCollection services, string environmentName = "Production")
    {
        var recording = new RecordingWebHostBuilder();
        new TelemetryHostingStartup().Configure(recording);

        var context = new WebHostBuilderContext
        {
            HostingEnvironment = new FakeWebHostEnvironment { EnvironmentName = environmentName },
            Configuration = new ConfigurationManager(),
        };

        recording.RunAppConfiguration(context, new ConfigurationManager());
        recording.RunServices(context, services);
    }

    [Fact]
    public void TheTypeImplementsIHostingStartupAndTheAssemblyCarriesTheAttribute()
    {
        Assert.True(typeof(IHostingStartup).IsAssignableFrom(typeof(TelemetryHostingStartup)));

        var attribute = typeof(TelemetryHostingStartup).Assembly
            .GetCustomAttributes(typeof(HostingStartupAttribute), inherit: false)
            .Cast<HostingStartupAttribute>()
            .SingleOrDefault(candidate => candidate.HostingStartupType == typeof(TelemetryHostingStartup));

        Assert.NotNull(attribute);
    }

    [Fact]
    public void ReferencingThePackageWithoutTheEnvVarInstrumentsNothing()
    {
        // A plain builder with no activation at all: proves the attribute is inert until the
        // hosting-startup mechanism (driven by the env var) invokes Configure.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });

        Assert.Equal(0, MarkerCount(builder.Services));
        Assert.Equal(0, StartupFilterCount(builder.Services));
    }

    [Fact]
    public void TheHostingStartupWiresTheSameServicesAsTheExtension()
    {
        var viaStartup = new ServiceCollection();
        ActivateHostingStartup(viaStartup);

        var viaExtension = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        viaExtension.AddBeehiveTelemetry();

        // Both register the marker and exactly one access-log start-up filter.
        Assert.Equal(1, MarkerCount(viaStartup));
        Assert.Equal(1, StartupFilterCount(viaStartup));
        Assert.Contains(viaStartup, descriptor => descriptor.ServiceType == typeof(IStartupFilter));

        Assert.Equal(MarkerCount(viaExtension.Services), MarkerCount(viaStartup));
        Assert.Equal(StartupFilterCount(viaExtension.Services), StartupFilterCount(viaStartup));
    }

    [Fact]
    public void HttpLogOffSkipsTheMiddlewareOnTheHostingStartupPathToo()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG", "off");

        var services = new ServiceCollection();
        ActivateHostingStartup(services);

        Assert.Equal(1, MarkerCount(services));
        Assert.Equal(0, StartupFilterCount(services));
    }

    [Fact]
    public void EnvVarPathThenExplicitAddIsInstrumentedExactlyOnce()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });

        // Hosting startup runs first (env-var path), then the app also calls the extension.
        ActivateHostingStartup(builder.Services);
        builder.AddBeehiveTelemetry();

        Assert.Equal(1, MarkerCount(builder.Services));
        Assert.Equal(1, StartupFilterCount(builder.Services));
    }

    [Fact]
    public void ExplicitAddThenEnvVarPathIsInstrumentedExactlyOnce()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });

        // The app calls the extension first, then the hosting startup runs at build time.
        builder.AddBeehiveTelemetry();
        ActivateHostingStartup(builder.Services);

        Assert.Equal(1, MarkerCount(builder.Services));
        Assert.Equal(1, StartupFilterCount(builder.Services));
    }

    /// <summary>Captures the callbacks a hosting startup registers so a test can run them.</summary>
    private sealed class RecordingWebHostBuilder : IWebHostBuilder
    {
        private readonly List<Action<WebHostBuilderContext, IConfigurationBuilder>> appConfig = [];
        private readonly List<Action<WebHostBuilderContext, IServiceCollection>> services = [];

        public void RunAppConfiguration(WebHostBuilderContext context, IConfigurationBuilder configuration)
        {
            foreach (var callback in appConfig)
            {
                callback(context, configuration);
            }
        }

        public void RunServices(WebHostBuilderContext context, IServiceCollection collection)
        {
            foreach (var callback in services)
            {
                callback(context, collection);
            }
        }

        public IWebHostBuilder ConfigureAppConfiguration(Action<WebHostBuilderContext, IConfigurationBuilder> configureDelegate)
        {
            appConfig.Add(configureDelegate);
            return this;
        }

        public IWebHostBuilder ConfigureServices(Action<IServiceCollection> configureServices)
        {
            services.Add((_, collection) => configureServices(collection));
            return this;
        }

        public IWebHostBuilder ConfigureServices(Action<WebHostBuilderContext, IServiceCollection> configureServices)
        {
            services.Add(configureServices);
            return this;
        }

        public Microsoft.AspNetCore.Hosting.IWebHost Build() => throw new NotSupportedException();

        public string? GetSetting(string key) => null;

        public IWebHostBuilder UseSetting(string key, string? value) => this;
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";

        public string ApplicationName { get; set; } = "test-app";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

using Beehive.Telemetry.Http;
using Beehive.Telemetry.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Beehive.Telemetry.Tests;

/// <summary>
/// Every knob falls back LOUDLY. A typo in a deployment variable must never silently
/// disable evidence capture or mute the logger.
/// </summary>
public class EnvValidationTests : IDisposable
{
    private static readonly string[] Owned =
    [
        "HTTP_LOG", "HTTP_LOG_PAYLOAD", "HTTP_LOG_SLOW_MS", "HTTP_LOG_BODY_MAX", "HTTP_LOG_BODY_MODE",
        "HTTP_LOG_PAYLOAD_ROUTES", "HTTP_LOG_IGNORE_PATHS", "LOG_LEVEL", "LOG_FORMAT", "FLY_APP_NAME",
    ];

    private readonly Dictionary<string, string?> saved = [];
    private readonly TextWriter originalError = Console.Error;
    private readonly StringWriter capturedError = new();

    public EnvValidationTests()
    {
        foreach (var name in Owned)
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        Console.SetError(capturedError);
    }

    public void Dispose()
    {
        Console.SetError(originalError);
        foreach (var pair in saved)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        capturedError.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Warnings => capturedError.ToString();

    [Fact]
    public void DefaultsAreTheDocumentedOnes()
    {
        var options = HttpLogOptions.FromEnvironment("svc");

        Assert.True(options.Enabled);
        Assert.Equal(PayloadMode.Always, options.PayloadMode);
        Assert.Equal(1000, options.SlowMs);
        Assert.Equal(4096, options.BodyMax);
        Assert.Equal(BodyMode.String, options.BodyMode);
        Assert.Equal(["/", "/health", "/healthz", "/favicon.ico"], options.IgnorePaths);
        Assert.Empty(Warnings);
    }

    [Theory]
    [InlineData("OFF")]
    [InlineData("Off")]
    [InlineData("off")]
    public void HttpLogOffIsCaseInsensitive(string value)
    {
        Environment.SetEnvironmentVariable("HTTP_LOG", value);

        Assert.False(HttpLogOptions.FromEnvironment("svc").Enabled);
    }

    [Fact]
    public void InvalidHttpLogFallsBackToOnLoudly()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG", "banana");

        Assert.True(HttpLogOptions.FromEnvironment("svc").Enabled);
        Assert.Contains("invalid HTTP_LOG \"banana\"", Warnings, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPayloadModeFallsBackToAlwaysLoudly()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_PAYLOAD", "banana");

        Assert.Equal(PayloadMode.Always, HttpLogOptions.FromEnvironment("svc").PayloadMode);
        Assert.Contains("invalid HTTP_LOG_PAYLOAD \"banana\" — using \"always\" (valid: always|errors|off)", Warnings, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ERRORS", "Errors")]
    [InlineData("Off", "Off")]
    [InlineData("always", "Always")]
    public void PayloadModeIsCaseInsensitive(string value, string expected)
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_PAYLOAD", value);

        Assert.Equal(expected, HttpLogOptions.FromEnvironment("svc").PayloadMode.ToString());
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("-1")]
    [InlineData("")]
    public void InvalidSlowMsFallsBackTo1000(string value)
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_SLOW_MS", value);

        Assert.Equal(1000, HttpLogOptions.FromEnvironment("svc").SlowMs);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("0")]
    [InlineData("-5")]
    public void InvalidBodyMaxFallsBackTo4096(string value)
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_BODY_MAX", value);

        Assert.Equal(4096, HttpLogOptions.FromEnvironment("svc").BodyMax);
        Assert.Contains("invalid HTTP_LOG_BODY_MAX", Warnings, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidBodyModeFallsBackToStringLoudly()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_BODY_MODE", "banana");

        Assert.Equal(BodyMode.String, HttpLogOptions.FromEnvironment("svc").BodyMode);
        Assert.Contains("invalid HTTP_LOG_BODY_MODE \"banana\"", Warnings, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectBodyModeIsAccepted()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_BODY_MODE", "OBJECT");

        Assert.Equal(BodyMode.Object, HttpLogOptions.FromEnvironment("svc").BodyMode);
    }

    [Fact]
    public void IgnorePathsMatchExactlyUnlessTheEntryEndsInASlash()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_IGNORE_PATHS", "/, /health, /internal/ , /exact");
        var options = HttpLogOptions.FromEnvironment("svc");

        Assert.True(options.IsIgnored("/"));
        Assert.True(options.IsIgnored("/health"));
        Assert.True(options.IsIgnored("/internal/anything/deep"));
        Assert.True(options.IsIgnored("/exact"));

        // bare "/" stays exact or it would match every path
        Assert.False(options.IsIgnored("/hello"));
        Assert.False(options.IsIgnored("/healthz"));
        Assert.False(options.IsIgnored("/exact/child"));
    }

    [Fact]
    public void PayloadRoutesAlwaysEnrichEvenInTheErrorsTier()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_PAYLOAD", "errors");
        Environment.SetEnvironmentVariable("HTTP_LOG_PAYLOAD_ROUTES", "/pay,/refund");
        var options = HttpLogOptions.FromEnvironment("svc");

        Assert.False(options.ShouldEnrich("/hello", 200, 5));
        Assert.True(options.ShouldEnrich("/hello", 500, 5));
        Assert.True(options.ShouldEnrich("/hello", 200, 1500));
        Assert.True(options.ShouldEnrich("/pay/authorize", 200, 5));
    }

    // --- LOG_LEVEL --------------------------------------------------------------
    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("INFO", LogLevel.Information)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    public void ValidLogLevelsAreAccepted(string value, LogLevel expected)
    {
        Environment.SetEnvironmentVariable("LOG_LEVEL", value);

        Assert.Equal(expected, LoggingSetup.ResolveLevel().Level);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("none")]
    [InlineData("off")]
    [InlineData("critical")]
    public void SilencingOrInvalidLogLevelsFallBackToInfoLoudly(string value)
    {
        Environment.SetEnvironmentVariable("LOG_LEVEL", value);

        var (level, explicitlySet) = LoggingSetup.ResolveLevel();

        // Rejecting none/off is deliberate: an audit event must always survive.
        Assert.Equal(LogLevel.Information, level);
        Assert.False(explicitlySet);
        Assert.Contains($"invalid LOG_LEVEL \"{value}\"", Warnings, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryValidLogLevelStillLetsCriticalThrough()
    {
        foreach (var value in new[] { "trace", "debug", "info", "warn", "error" })
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", value);
            Assert.True(LogLevel.Critical >= LoggingSetup.ResolveLevel().Level);
        }
    }

    // --- LOG_FORMAT -------------------------------------------------------------
    [Fact]
    public void JsonIsUsedOnAPlatformOrInProduction()
    {
        Assert.True(LoggingSetup.UseJson("Production"));
        Assert.False(LoggingSetup.UseJson("Development"));

        Environment.SetEnvironmentVariable("FLY_APP_NAME", "some-app");
        Assert.True(LoggingSetup.UseJson("Development"));
    }

    [Fact]
    public void LogFormatOverridesTheEnvironmentAndIsValidatedLoudly()
    {
        Environment.SetEnvironmentVariable("LOG_FORMAT", "pretty");
        Assert.False(LoggingSetup.UseJson("Production"));

        Environment.SetEnvironmentVariable("LOG_FORMAT", "JSON");
        Assert.True(LoggingSetup.UseJson("Development"));

        Environment.SetEnvironmentVariable("LOG_FORMAT", "banana");
        Assert.True(LoggingSetup.UseJson("Development"));
        Assert.Contains("invalid LOG_FORMAT \"banana\"", Warnings, StringComparison.Ordinal);
    }

    // --- empty means unset ------------------------------------------------------
    [Fact]
    public void EmptyValuesAreTreatedAsUnset()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_PAYLOAD", string.Empty);
        Environment.SetEnvironmentVariable("LOG_LEVEL", string.Empty);

        Assert.Equal(PayloadMode.Always, HttpLogOptions.FromEnvironment("svc").PayloadMode);
        Assert.Equal(LogLevel.Information, LoggingSetup.ResolveLevel().Level);
        Assert.Empty(Warnings);
    }
}

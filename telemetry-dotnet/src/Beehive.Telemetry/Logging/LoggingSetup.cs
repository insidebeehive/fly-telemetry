using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Beehive.Telemetry.Logging;

/// <summary>
/// Wires the console logging pipeline: JSON one-liners in production, a coloured
/// human-readable line locally, and a <c>LOG_LEVEL</c> that can never silence an audit event.
/// </summary>
internal static class LoggingSetup
{
    /// <summary>
    /// <c>LOG_LEVEL</c> values, JS-parity names. Deliberately no <c>none</c>/<c>off</c>:
    /// silencing the logger entirely would also silence audit events, so those are rejected
    /// like any other invalid value.
    /// </summary>
    private static readonly Dictionary<string, LogLevel> Levels = new(StringComparer.Ordinal)
    {
        ["trace"] = LogLevel.Trace,
        ["debug"] = LogLevel.Debug,
        ["info"] = LogLevel.Information,
        ["warn"] = LogLevel.Warning,
        ["error"] = LogLevel.Error,
    };

    internal static void Configure(ILoggingBuilder logging, string environmentName)
    {
        var (level, explicitlySet) = ResolveLevel();
        var json = UseJson(environmentName);

        logging.SetMinimumLevel(level);
        if (explicitlySet)
        {
            // LOG_LEVEL is the operator's word on this deployment, so it also overrides a
            // "Default" rule inherited from appsettings. More specific per-category rules
            // the app configured itself still win, as they should.
            logging.AddFilter((_, candidate) => candidate >= level);
        }

        if (json)
        {
            logging.AddConsoleFormatter<BeehiveJsonConsoleFormatter, ConsoleFormatterOptions>();
        }
        else
        {
            logging.AddConsoleFormatter<BeehivePrettyConsoleFormatter, ConsoleFormatterOptions>();
        }

        logging.AddConsole(options => options.FormatterName = json
            ? BeehiveJsonConsoleFormatter.FormatterName
            : BeehivePrettyConsoleFormatter.FormatterName);

        logging.Services.Configure<ConsoleFormatterOptions>(options => options.IncludeScopes = true);
    }

    /// <summary>
    /// Production means "logs are being collected": a platform app name is set, or the host
    /// says Production. <c>LOG_FORMAT</c> overrides, and is validated loudly.
    /// </summary>
    internal static bool UseJson(string environmentName)
    {
        var format = TelemetryEnv.Raw("LOG_FORMAT");
        if (format is not null)
        {
            var choice = TelemetryEnv.Choice("LOG_FORMAT", "json", "json", "pretty");
            return string.Equals(choice, "json", StringComparison.Ordinal);
        }

        return TelemetryEnv.Raw("FLY_APP_NAME") is not null
            || string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An invalid <c>LOG_LEVEL</c> must fall back loudly, not silently mute the logger.
    /// </summary>
    internal static (LogLevel Level, bool ExplicitlySet) ResolveLevel()
    {
        var raw = TelemetryEnv.Raw("LOG_LEVEL");
        if (raw is null)
        {
            return (LogLevel.Information, false);
        }

        if (Levels.TryGetValue(raw.Trim().ToLowerInvariant(), out var level))
        {
            return (level, true);
        }

        TelemetryEnv.Warn($"invalid LOG_LEVEL \"{raw}\" — using \"info\" (valid: {string.Join("|", Levels.Keys)})");
        return (LogLevel.Information, false);
    }
}

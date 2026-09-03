using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Beehive.Telemetry.Logging;

/// <summary>
/// Human-readable, coloured one-liner for local development:
/// <c>HH:mm:ss.fff level message {meta}</c>, with the stack on following lines.
/// </summary>
internal sealed class BeehivePrettyConsoleFormatter : ConsoleFormatter
{
    internal const string FormatterName = "beehive-pretty";

    private const string OriginalFormatKey = "{OriginalFormat}";
    private const string Reset = "\u001b[0m";

    private static readonly JsonSerializerOptions MetaJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly bool UseColour = DetectColourSupport();

    public BeehivePrettyConsoleFormatter()
        : base(FormatterName)
    {
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);
        try
        {
            var isAudit = IsAudit(logEntry.State);
            var level = isAudit ? "audit" : LogRecordBuilder.LevelName(logEntry.LogLevel);
            var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception) ?? logEntry.State?.ToString() ?? string.Empty;

            var meta = new JsonObject();
            scopeProvider?.ForEachScope(
                static (scope, target) => AddPairs(target, scope as IEnumerable<KeyValuePair<string, object?>>),
                meta);
            AddPairs(meta, logEntry.State as IEnumerable<KeyValuePair<string, object?>>);

            textWriter.Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            textWriter.Write(' ');
            if (UseColour)
            {
                textWriter.Write(Colour(level));
            }

            textWriter.Write(level);
            if (UseColour)
            {
                textWriter.Write(Reset);
            }

            textWriter.Write(' ');
            textWriter.Write(message);

            if (meta.Count > 0)
            {
                textWriter.Write(' ');
                textWriter.Write(meta.ToJsonString(MetaJson));
            }

            if (logEntry.Exception is not null)
            {
                textWriter.Write('\n');
                textWriter.Write(logEntry.Exception.ToString());
            }

            textWriter.Write('\n');
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("log formatting failed", error);
        }
    }

    private static void AddPairs(JsonObject meta, IEnumerable<KeyValuePair<string, object?>>? pairs)
    {
        if (pairs is null)
        {
            return;
        }

        foreach (var pair in pairs)
        {
            if (string.Equals(pair.Key, OriginalFormatKey, StringComparison.Ordinal)
                || string.Equals(pair.Key, AuditLoggerExtensions.AuditMarkerKey, StringComparison.Ordinal))
            {
                continue;
            }

            meta[pair.Key] = LogRecordBuilder.Value(pair.Value);
        }
    }

    private static bool IsAudit<TState>(TState state) => state is AuditLoggerExtensions.AuditState;

    private static string Colour(string level) => level switch
    {
        "trace" => "\u001b[90m",
        "debug" => "\u001b[34m",
        "info" => "\u001b[32m",
        "warn" => "\u001b[33m",
        "error" => "\u001b[31m",
        "critical" => "\u001b[91m",
        "audit" => "\u001b[35m",
        _ => "\u001b[37m",
    };

    private static bool DetectColourSupport()
    {
        try
        {
            // Honour the de-facto standard opt-out, and never emit escapes into a pipe.
            return TelemetryEnv.Raw("NO_COLOR") is null && !Console.IsOutputRedirected;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

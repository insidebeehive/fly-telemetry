using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Beehive.Telemetry.Logging;

/// <summary>
/// One JSON object per log entry on stdout — the production format.
/// </summary>
/// <remarks>
/// <para>
/// Shape: <c>level</c>, <c>message</c>, <c>timestamp</c>, <c>logger</c> (always
/// <c>app</c> — the stream-level partner of the http logger's <c>http</c>),
/// <c>service</c>, the current <c>trace_id</c>/<c>span_id</c>, every state and scope
/// key/value flattened as a top-level field, and finally the <c>category</c>.
/// </para>
/// <para>
/// <c>trace_id</c> appears automatically on lines logged inside a request — the pivot
/// between an app log, its <c>http.access</c> line and its spans.
/// </para>
/// </remarks>
internal sealed class BeehiveJsonConsoleFormatter : ConsoleFormatter
{
    internal const string FormatterName = "beehive-json";

    private const string OriginalFormatKey = "{OriginalFormat}";

    private static readonly JsonSerializerOptions LineJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public BeehiveJsonConsoleFormatter()
        : base(FormatterName)
    {
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);
        try
        {
            textWriter.Write(Render(logEntry, scopeProvider));
            textWriter.Write('\n');
        }
        catch (Exception error)
        {
            // A formatting failure must never take the app with it, and must not be
            // silent either.
            TelemetryEnv.Warn("log formatting failed", error);
        }
    }

    private static string Render<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider)
    {
        var isAudit = IsAudit(logEntry.State);
        var record = new JsonObject
        {
            ["level"] = isAudit ? "audit" : LogRecordBuilder.LevelName(logEntry.LogLevel),
            ["message"] = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception) ?? logEntry.State?.ToString() ?? string.Empty,
            ["timestamp"] = RawLog.Timestamp(),

            // The stream discriminator; convention is http | app | (absent).
            ["logger"] = "app",
            ["service"] = ServiceName.Resolve(),
        };

        if (RawLog.TryActivityIds(out var traceId, out var spanId, out _))
        {
            record["trace_id"] = traceId;
            record["span_id"] = spanId;
        }

        // Scopes first, so a more specific state field wins over an outer scope's field.
        scopeProvider?.ForEachScope(
            static (scope, target) => AddPairs(target, scope as IEnumerable<KeyValuePair<string, object?>>),
            record);

        AddPairs(record, logEntry.State as IEnumerable<KeyValuePair<string, object?>>);

        if (logEntry.Exception is not null)
        {
            record["err"] = LogRecordBuilder.Error(logEntry.Exception);
        }

        record["category"] = logEntry.Category;
        return record.ToJsonString(LineJson);
    }

    private static void AddPairs(JsonObject record, IEnumerable<KeyValuePair<string, object?>>? pairs)
    {
        if (pairs is null)
        {
            return;
        }

        foreach (var pair in pairs)
        {
            // {OriginalFormat} is the template, already rendered into `message`.
            if (string.Equals(pair.Key, OriginalFormatKey, StringComparison.Ordinal)
                || string.Equals(pair.Key, AuditLoggerExtensions.AuditMarkerKey, StringComparison.Ordinal))
            {
                continue;
            }

            record[pair.Key] = LogRecordBuilder.Value(pair.Value);
        }
    }

    private static bool IsAudit<TState>(TState state)
    {
        if (state is AuditLoggerExtensions.AuditState)
        {
            return true;
        }

        if (state is not IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            return false;
        }

        foreach (var pair in pairs)
        {
            if (string.Equals(pair.Key, AuditLoggerExtensions.AuditMarkerKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

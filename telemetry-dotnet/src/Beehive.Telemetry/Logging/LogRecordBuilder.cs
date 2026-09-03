using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Beehive.Telemetry.Logging;

/// <summary>
/// Shared shaping rules for app log lines, so the JSON formatter, the pretty formatter and
/// the crash handlers all agree on level names, error rendering and value conversion.
/// </summary>
internal static class LogRecordBuilder
{
    /// <summary>Level names, lower-cased (JS parity: warn, not warning).</summary>
    internal static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "none",
    };

    /// <summary>The <c>err</c> field: message plus a full stack.</summary>
    internal static JsonObject Error(Exception error) => new()
    {
        ["message"] = error.Message,

        // ToString() carries inner exceptions and their stacks too, which a bare
        // StackTrace would drop.
        ["stack"] = error.ToString(),
    };

    /// <summary>
    /// Converts a log state/scope value to JSON. Primitives keep their type; anything else
    /// is rendered as text rather than reflected over — a log line must never be able to
    /// throw or recurse on a caller's object graph.
    /// </summary>
    internal static JsonNode? Value(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        short number => JsonValue.Create(number),
        byte number => JsonValue.Create(number),
        sbyte number => JsonValue.Create(number),
        uint number => JsonValue.Create(number),
        ulong number => JsonValue.Create(number),
        ushort number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        float number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        Guid guid => JsonValue.Create(guid.ToString()),
        DateTime moment => JsonValue.Create(moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
        DateTimeOffset moment => JsonValue.Create(moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
        TimeSpan span => JsonValue.Create(span.ToString(null, CultureInfo.InvariantCulture)),
        Exception error => Error(error),
        JsonNode node => node.DeepClone(),
        _ => JsonValue.Create(Text(value)),
    };

    private static string? Text(object value)
    {
        try
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return value.GetType().Name;
        }
    }
}

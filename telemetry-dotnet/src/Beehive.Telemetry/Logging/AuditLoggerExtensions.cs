using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Beehive.Telemetry;

/// <summary>
/// <c>audit</c> logging: business events that must survive any log-level setting.
/// </summary>
/// <remarks>
/// <para>
/// Audit is a LEVEL, not a stream: lines stay in <c>logger=app</c> and are selected with
/// <c>level:audit</c>. They are emitted at <see cref="LogLevel.Critical"/> — which passes
/// every value <c>LOG_LEVEL</c> accepts — and the package's console formatter rewrites the
/// rendered level to <c>audit</c>. An app quieted to <c>LOG_LEVEL=error</c> still records
/// every audit event.
/// </para>
/// <example>
/// <code>
/// logger.Audit("order.settled", new { actor = "cron", orderId, amount });
/// </code>
/// </example>
/// </remarks>
public static class AuditLoggerExtensions
{
    /// <summary>State key that marks an entry as an audit event.</summary>
    internal const string AuditMarkerKey = "beehive.audit";

    /// <summary>
    /// Records an audit event with an optional bag of context fields, which are flattened
    /// onto the line as top-level properties.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="event">Event name, e.g. <c>order.settled</c>. Becomes the line's message.</param>
    /// <param name="data">
    /// Any object; its public properties become log fields. Anonymous types are the intended
    /// shape: <c>new { actor, orderId, amount }</c>.
    /// </param>
    public static void Audit(
        this ILogger logger,
        string @event,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] object? data = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Audit(logger, @event, Flatten(data));
    }

    /// <summary>
    /// Records an audit event with explicit fields.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="event">Event name, e.g. <c>order.settled</c>. Becomes the line's message.</param>
    /// <param name="fields">Context fields, flattened onto the line as top-level properties.</param>
    public static void Audit(this ILogger logger, string @event, IEnumerable<KeyValuePair<string, object?>>? fields)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            var state = new AuditState(@event ?? string.Empty, fields);

            // Critical, so no LOG_LEVEL value can silence it. The formatter renders the
            // level as "audit" because of the marker carried in the state.
            logger.Log(LogLevel.Critical, default, state, null, static (s, _) => s.Message);
        }
        catch (Exception error)
        {
            TelemetryEnv.Warn("audit log failed", error);
        }
    }

    private static List<KeyValuePair<string, object?>>? Flatten(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] object? data)
    {
        if (data is null)
        {
            return null;
        }

        if (data is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            return [.. pairs];
        }

        try
        {
            var result = new List<KeyValuePair<string, object?>>();
            foreach (var property in data.GetType().GetProperties())
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                result.Add(new KeyValuePair<string, object?>(property.Name, property.GetValue(data)));
            }

            return result;
        }
        catch (Exception)
        {
            return [new KeyValuePair<string, object?>("data", data.ToString())];
        }
    }

    /// <summary>
    /// Log state for an audit entry: the caller's fields plus the marker the formatter looks
    /// for. Implemented as a key/value list so any <c>ILogger</c> provider — not just this
    /// package's formatter — sees structured properties.
    /// </summary>
    internal sealed class AuditState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> items;

        internal AuditState(string message, IEnumerable<KeyValuePair<string, object?>>? fields)
        {
            Message = message;
            items = [];
            if (fields is not null)
            {
                items.AddRange(fields);
            }

            items.Add(new KeyValuePair<string, object?>(AuditMarkerKey, true));
        }

        internal string Message { get; }

        public int Count => items.Count;

        public KeyValuePair<string, object?> this[int index] => items[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Message;
    }
}

using System.Text.Json.Nodes;
using Beehive.Telemetry.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Xunit;

namespace Beehive.Telemetry.Tests;

/// <summary>
/// The app-log line shape: lower-case level names, the audit rewrite, error serialisation,
/// and state flattened as top-level fields.
/// </summary>
public class FormatterTests
{
    private static readonly ConsoleFormatter Json = new BeehiveJsonConsoleFormatter();

    private static JsonObject Render<TState>(LogLevel level, string category, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        using var writer = new StringWriter();
        Json.Write(new LogEntry<TState>(level, category, default, state, exception, formatter), null, writer);
        var text = writer.ToString();
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        return (JsonObject)JsonNode.Parse(text)!;
    }

    private static JsonObject RenderMessage(LogLevel level, string message, params KeyValuePair<string, object?>[] state)
    {
        var list = state.ToList();
        return Render(level, "test.category", list, null, (_, _) => message);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "trace")]
    [InlineData(LogLevel.Debug, "debug")]
    [InlineData(LogLevel.Information, "info")]
    [InlineData(LogLevel.Warning, "warn")]
    [InlineData(LogLevel.Error, "error")]
    [InlineData(LogLevel.Critical, "critical")]
    public void LevelNamesAreLowerCasedAndAbbreviated(LogLevel level, string expected) =>
        Assert.Equal(expected, RenderMessage(level, "hello")["level"]!.GetValue<string>());

    [Fact]
    public void TheLineCarriesTheStreamDiscriminatorAndService()
    {
        var record = RenderMessage(LogLevel.Information, "order placed");

        Assert.Equal("info", record["level"]!.GetValue<string>());
        Assert.Equal("order placed", record["message"]!.GetValue<string>());
        Assert.Equal("app", record["logger"]!.GetValue<string>());
        Assert.Equal("dotnet", record["runtime"]!.GetValue<string>());
        Assert.Equal("test.category", record["category"]!.GetValue<string>());
        Assert.NotNull(record["service"]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", record["timestamp"]!.GetValue<string>());
    }

    [Fact]
    public void FieldsComeFirstInTheDocumentedOrder()
    {
        var record = RenderMessage(LogLevel.Information, "hello", new KeyValuePair<string, object?>("orderId", "o_991"));
        var keys = record.Select(pair => pair.Key).ToArray();

        Assert.Equal("level", keys[0]);
        Assert.Equal("message", keys[1]);
        Assert.Equal("timestamp", keys[2]);
        Assert.Equal("logger", keys[3]);
        Assert.Equal("service", keys[4]);
        Assert.Equal("category", keys[^1]);
    }

    [Fact]
    public void StateKeyValuesAreFlattenedAsTopLevelFields()
    {
        var record = RenderMessage(
            LogLevel.Information,
            "order placed",
            new KeyValuePair<string, object?>("orderId", "o_991"),
            new KeyValuePair<string, object?>("amount", 250),
            new KeyValuePair<string, object?>("{OriginalFormat}", "order placed {orderId}"));

        Assert.Equal("o_991", record["orderId"]!.GetValue<string>());
        Assert.Equal(250, record["amount"]!.GetValue<int>());
        Assert.False(record.ContainsKey("{OriginalFormat}"));
    }

    [Fact]
    public void ExceptionsBecomeAnErrObjectWithAFullStack()
    {
        Exception captured;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException error)
        {
            captured = error;
        }

        var record = Render(LogLevel.Error, "test.category", new List<KeyValuePair<string, object?>>(), captured, (_, _) => "payment failed");

        var err = record["err"]!.AsObject();
        Assert.Equal("boom", err["message"]!.GetValue<string>());
        Assert.Contains("InvalidOperationException", err["stack"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("ExceptionsBecomeAnErrObjectWithAFullStack", err["stack"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionsNestedInStateAreSerialisedToo()
    {
        var record = RenderMessage(
            LogLevel.Error,
            "payment capture failed",
            new KeyValuePair<string, object?>("err", new TimeoutException("upstream 504")));

        Assert.Equal("upstream 504", record["err"]!["message"]!.GetValue<string>());
        Assert.Contains("TimeoutException", record["err"]!["stack"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // --- audit -------------------------------------------------------------------
    [Fact]
    public void AuditLogsAtCriticalAndRendersAsTheAuditLevel()
    {
        var logger = new CapturingLogger();

        logger.Audit("order.settled", new { actor = "cron", orderId = "o_991", amount = 250 });

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Equal("order.settled", entry.Message);

        var record = (JsonObject)JsonNode.Parse(entry.Render(Json))!;
        Assert.Equal("audit", record["level"]!.GetValue<string>());
        Assert.Equal("order.settled", record["message"]!.GetValue<string>());
        Assert.Equal("cron", record["actor"]!.GetValue<string>());
        Assert.Equal("o_991", record["orderId"]!.GetValue<string>());
        Assert.Equal(250, record["amount"]!.GetValue<int>());

        // The marker is plumbing, not a field.
        Assert.False(record.ContainsKey("beehive.audit"));
    }

    [Fact]
    public void AuditWorksWithoutContextFields()
    {
        var logger = new CapturingLogger();

        logger.Audit("service.started");

        var record = (JsonObject)JsonNode.Parse(Assert.Single(logger.Entries).Render(Json))!;
        Assert.Equal("audit", record["level"]!.GetValue<string>());
        Assert.Equal("service.started", record["message"]!.GetValue<string>());
    }

    [Fact]
    public void AuditAcceptsExplicitFields()
    {
        var logger = new CapturingLogger();

        logger.Audit("order.settled", new[] { new KeyValuePair<string, object?>("actor", "cron") });

        var record = (JsonObject)JsonNode.Parse(Assert.Single(logger.Entries).Render(Json))!;
        Assert.Equal("cron", record["actor"]!.GetValue<string>());
    }

    [Fact]
    public void OrdinaryCriticalEntriesAreNotRewrittenToAudit() =>
        Assert.Equal("critical", RenderMessage(LogLevel.Critical, "database unreachable")["level"]!.GetValue<string>());

    [Fact]
    public void ScopeValuesAreFlattenedToo()
    {
        using var writer = new StringWriter();
        var scopes = new LoggerExternalScopeProvider();
        using (scopes.Push(new[] { new KeyValuePair<string, object?>("module", "wallet") }))
        {
            var state = new List<KeyValuePair<string, object?>> { new("orderId", "o_1") };
            Json.Write(new LogEntry<List<KeyValuePair<string, object?>>>(LogLevel.Information, "cat", default, state, null, (_, _) => "m"), scopes, writer);
        }

        var record = (JsonObject)JsonNode.Parse(writer.ToString())!;
        Assert.Equal("wallet", record["module"]!.GetValue<string>());
        Assert.Equal("o_1", record["orderId"]!.GetValue<string>());
    }

    [Fact]
    public void TheLineIsAlwaysExactlyOneJsonObject()
    {
        var record = RenderMessage(LogLevel.Information, "multi\nline\nmessage");

        Assert.Equal("multi\nline\nmessage", record["message"]!.GetValue<string>());
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string Render(ConsoleFormatter target)
            {
                using var writer = new StringWriter();
                target.Write(new LogEntry<TState>(logLevel, "test.category", eventId, state, exception, formatter), null, writer);
                return writer.ToString();
            }

            Entries.Add(new Entry(logLevel, formatter(state, exception), Render));
        }
    }

    private sealed record Entry(LogLevel Level, string Message, Func<ConsoleFormatter, string> Render);
}

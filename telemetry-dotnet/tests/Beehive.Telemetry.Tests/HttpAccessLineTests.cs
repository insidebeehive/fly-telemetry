using System.Text;
using System.Text.Json.Nodes;
using Beehive.Telemetry.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace Beehive.Telemetry.Tests;

/// <summary>
/// The <c>http.access</c> contract, exercised through the real middleware: field names,
/// field order, one line per request, and the status a client actually saw.
/// </summary>
public class HttpAccessLineTests : IDisposable
{
    private static readonly string[] Owned =
    [
        "HTTP_LOG", "HTTP_LOG_PAYLOAD", "HTTP_LOG_SLOW_MS", "HTTP_LOG_BODY_MAX", "HTTP_LOG_BODY_MODE",
        "HTTP_LOG_PAYLOAD_ROUTES", "HTTP_LOG_IGNORE_PATHS",
    ];

    private readonly Dictionary<string, string?> saved = [];
    private readonly TextWriter originalOut = Console.Out;

    public HttpAccessLineTests()
    {
        foreach (var name in Owned)
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        Console.SetOut(originalOut);
        foreach (var pair in saved)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        GC.SuppressFinalize(this);
    }

    private sealed record Result(IReadOnlyList<JsonObject> Lines, Exception? Rethrown)
    {
        public JsonObject Single => Assert.Single(Lines);
    }

    private static async Task<Result> RunAsync(Action<HttpContext> arrange, RequestDelegate handler)
    {
        var options = HttpLogOptions.FromEnvironment("test-service");
        var middleware = new HttpAccessLogMiddleware(handler, options);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/hello"; // "/" is on the default ignore list
        context.Response.Body = new MemoryStream();
        arrange(context);

        var captured = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(captured);
        Exception? rethrown = null;
        try
        {
            await middleware.InvokeAsync(context);
        }
        catch (Exception error)
        {
            rethrown = error;
        }
        finally
        {
            Console.SetOut(previous);
        }

        var lines = captured.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (JsonObject)JsonNode.Parse(line)!)
            .ToList();

        return new Result(lines, rethrown);
    }

    private static Task WriteJsonAsync(HttpContext context, string json)
    {
        context.Response.ContentType = "application/json";
        return context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json)).AsTask();
    }

    // --- shape ------------------------------------------------------------------
    [Fact]
    public async Task BareLineCarriesTheContractFieldsInOrder()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_PAYLOAD", "off");

        var result = await RunAsync(
            context =>
            {
                context.Request.Method = "POST";
                context.Request.Path = "/api/orders";
                context.Request.QueryString = new QueryString("?page=2&token=tk-1");
                context.Request.Headers["Host"] = "api.example.com";
                context.Request.Headers["X-Forwarded-For"] = "203.0.113.7, 10.0.0.1";
                context.SetEndpoint(new RouteEndpoint(
                    _ => Task.CompletedTask,
                    RoutePatternFactory.Parse("/api/orders/{id}"),
                    0,
                    null,
                    "orders"));
            },
            async context =>
            {
                context.Response.StatusCode = 201;
                await WriteJsonAsync(context, """{"id":1}""");
            });

        var line = result.Single;
        var keys = line.Select(pair => pair.Key).ToArray();

        // Field NAMES are the contract: ts (not timestamp), http_host (not host), logger=http.
        Assert.Equal(
            new[] { "level", "message", "ts", "logger", "service", "method", "path", "route", "url", "http_host", "status", "duration_ms", "ip", "res_bytes" },
            keys);

        Assert.Equal("info", line["level"]!.GetValue<string>());
        Assert.Equal("http.access", line["message"]!.GetValue<string>());
        Assert.Equal("http", line["logger"]!.GetValue<string>());
        Assert.Equal("test-service", line["service"]!.GetValue<string>());
        Assert.Equal("POST", line["method"]!.GetValue<string>());
        Assert.Equal("/api/orders", line["path"]!.GetValue<string>());
        Assert.Equal("/api/orders/{id}", line["route"]!.GetValue<string>());
        Assert.Equal("/api/orders?page=2&token=[REDACTED]", line["url"]!.GetValue<string>());
        Assert.Equal("api.example.com", line["http_host"]!.GetValue<string>());
        Assert.Equal(201, line["status"]!.GetValue<int>());
        Assert.Equal("203.0.113.7", line["ip"]!.GetValue<string>());
        Assert.Equal(8, line["res_bytes"]!.GetValue<int>());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", line["ts"]!.GetValue<string>());
    }

    [Fact]
    public async Task DurationIsRoundedToOneDecimal()
    {
        var result = await RunAsync(_ => { }, _ => Task.CompletedTask);
        var duration = result.Single["duration_ms"]!.GetValue<double>();

        Assert.Equal(Math.Round(duration, 1), duration);
    }

    [Fact]
    public async Task RouteIsOmittedWhenNoEndpointResolved()
    {
        var result = await RunAsync(_ => { }, _ => Task.CompletedTask);

        Assert.False(result.Single.ContainsKey("route"));
    }

    [Fact]
    public async Task RemoteAddressIsUsedWhenThereIsNoForwardedForHeader()
    {
        var result = await RunAsync(
            context => context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.4"),
            _ => Task.CompletedTask);

        Assert.Equal("198.51.100.4", result.Single["ip"]!.GetValue<string>());
    }

    // --- ignore list --------------------------------------------------------------
    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/healthz")]
    [InlineData("/favicon.ico")]
    public async Task IgnoredPathsEmitNothing(string path)
    {
        var result = await RunAsync(context => context.Request.Path = path, _ => Task.CompletedTask);

        Assert.Empty(result.Lines);
    }

    // --- payload enrichment --------------------------------------------------------
    [Fact]
    public async Task EnrichedLineCarriesHeadersAndBodiesOnTheSameLine()
    {
        var result = await RunAsync(
            context =>
            {
                context.Request.Method = "POST";
                context.Request.Path = "/bets";
                context.Request.ContentType = "application/json";
                context.Request.Headers["Cookie"] = "sid=secret-cookie-value";
                context.Request.Headers["Authorization"] = "Bearer tok-abc123";
                var body = Encoding.UTF8.GetBytes("""{"password":"pw-1","card_no":"4242424242424242","amount":250}""");
                context.Request.Body = new MemoryStream(body);
                context.Request.ContentLength = body.Length;
            },
            context => WriteJsonAsync(context, """{"ok":true,"session_token":"st-9"}"""));

        var line = result.Single;

        Assert.True(line["payload"]!.GetValue<bool>());
        Assert.Equal("""{"password":"[REDACTED]","card_no":"4242424242424242","amount":250}""", line["req_body"]!.GetValue<string>());
        Assert.Equal("""{"ok":true,"session_token":"[REDACTED]"}""", line["res_body"]!.GetValue<string>());

        var requestHeaders = line["req_headers"]!.AsObject().Select(pair => pair.Key).ToList();
        Assert.DoesNotContain("cookie", requestHeaders, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", requestHeaders, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("application/json", line["res_headers"]!["content-type"]!.GetValue<string>());
    }

    [Fact]
    public async Task RequestBodyIsRewoundSoTheApplicationStillReadsIt()
    {
        var seen = string.Empty;
        var body = Encoding.UTF8.GetBytes("""{"amount":250}""");

        await RunAsync(
            context =>
            {
                context.Request.Method = "POST";
                context.Request.Path = "/bets";
                context.Request.ContentType = "application/json";
                context.Request.Body = new MemoryStream(body);
                context.Request.ContentLength = body.Length;
            },
            async context =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                seen = await reader.ReadToEndAsync();
            });

        Assert.Equal("""{"amount":250}""", seen);
    }

    [Fact]
    public async Task BodiesBeyondTheCapAreFlaggedAndStillScrubbed()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_BODY_MAX", "64");
        var body = Encoding.UTF8.GetBytes("""{"token":"tk-early","pad":" """ + new string('x', 200) + """ ","late":"v"}""");

        var result = await RunAsync(
            context =>
            {
                context.Request.Method = "POST";
                context.Request.Path = "/bets";
                context.Request.ContentType = "application/json";
                context.Request.Body = new MemoryStream(body);
                context.Request.ContentLength = body.Length;
            },
            context => WriteJsonAsync(context, """{"ok":true}"""));

        var line = result.Single;
        Assert.True(line["req_body_truncated"]!.GetValue<bool>());
        Assert.DoesNotContain("tk-early", line["req_body"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PayloadOffLeavesTheLineBareButStillCountsBytes()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_PAYLOAD", "off");

        var result = await RunAsync(_ => { }, context => WriteJsonAsync(context, """{"a":1}"""));
        var line = result.Single;

        Assert.False(line.ContainsKey("payload"));
        Assert.False(line.ContainsKey("req_headers"));
        Assert.False(line.ContainsKey("res_body"));
        Assert.Equal(7, line["res_bytes"]!.GetValue<int>());
    }

    [Fact]
    public async Task ErrorsTierEnrichesFailuresOnly()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_PAYLOAD", "errors");

        var ok = await RunAsync(context => context.Request.Path = "/hello", context => WriteJsonAsync(context, """{"a":1}"""));
        Assert.False(ok.Single.ContainsKey("payload"));

        var bad = await RunAsync(
            context => context.Request.Path = "/hello",
            context =>
            {
                context.Response.StatusCode = 500;
                return WriteJsonAsync(context, """{"error":"boom"}""");
            });
        Assert.True(bad.Single["payload"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ObjectBodyModeLandsAsNestedFields()
    {
        Environment.SetEnvironmentVariable("HTTP_LOG_BODY_MODE", "object");

        var result = await RunAsync(_ => { }, context => WriteJsonAsync(context, """{"amount":250,"token":"t"}"""));

        var body = result.Single["res_body"]!.AsObject();
        Assert.Equal(250, body["amount"]!.GetValue<int>());
        Assert.Equal("[REDACTED]", body["token"]!.GetValue<string>());
    }

    // --- failure and abort paths ----------------------------------------------------
    [Fact]
    public async Task UnhandledExceptionsAreLoggedAs500AndRethrown()
    {
        var result = await RunAsync(
            context => context.Request.Path = "/crash",
            _ => throw new InvalidOperationException("smoke: unhandled exception"));

        Assert.Equal(500, result.Single["status"]!.GetValue<int>());
        Assert.IsType<InvalidOperationException>(result.Rethrown);
        Assert.Equal("smoke: unhandled exception", result.Rethrown!.Message);
    }

    [Fact]
    public async Task AbortedRequestsAreLoggedAs499()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var result = await RunAsync(
            context =>
            {
                context.Request.Path = "/slow";
                context.Features.Set<IHttpRequestLifetimeFeature>(new AbortedLifetime(aborted.Token));
            },
            _ => Task.CompletedTask);

        Assert.Equal(499, result.Single["status"]!.GetValue<int>());
        Assert.True(result.Single["aborted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AbortedRequestsThatThrowCancellationAreStill499()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var result = await RunAsync(
            context =>
            {
                context.Request.Path = "/slow";
                context.Features.Set<IHttpRequestLifetimeFeature>(new AbortedLifetime(aborted.Token));
            },
            context => Task.FromCanceled(context.RequestAborted));

        Assert.Equal(499, result.Single["status"]!.GetValue<int>());
        Assert.True(result.Single["aborted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExactlyOneLineIsEmittedPerRequest()
    {
        var result = await RunAsync(_ => { }, context => WriteJsonAsync(context, """{"a":1}"""));

        Assert.Single(result.Lines);
    }

    private sealed class AbortedLifetime : IHttpRequestLifetimeFeature
    {
        public AbortedLifetime(CancellationToken token) => RequestAborted = token;

        public CancellationToken RequestAborted { get; set; }

        public void Abort()
        {
        }
    }
}

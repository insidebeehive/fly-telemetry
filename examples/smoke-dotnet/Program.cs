using System.Text;
using System.Text.Json.Nodes;
using Beehive.Telemetry;

// ---------------------------------------------------------------------------
// Smoke-test app for Beehive.Telemetry.
//
// The ONLY telemetry code in this file is the AddBeehiveTelemetry() line below.
// There is no app.UseSomething() for the access log — the package installs its
// middleware first in the pipeline through an IStartupFilter.
//
//   GET  /health -> "ok"      (on the ignore list: no access line, no span)
//   GET  /hello  -> json      (plain request; ?query params exercise redaction)
//   POST /bets   -> echoes the posted body back as json
//   GET  /error  -> 500
//   GET  /slow   -> 1.5s delay
//   GET  /crash  -> throws (one access line, then the exception propagates)
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.AddBeehiveTelemetry();

// Not part of the integration: ASP.NET Core's own "Request starting/finished" logging is a
// second access log, which the http.access line replaces. The default project template's
// appsettings.json does this for you; this sample has no appsettings.json.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("smoke");

app.MapGet("/health", () => "ok");

app.MapGet("/hello", () =>
{
    logger.LogInformation("hello handled {Greeting}", "world");
    return Results.Json(new { hello = "world" });
});

app.MapPost("/bets", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
    var raw = await reader.ReadToEndAsync();

    JsonNode? parsed = null;
    try
    {
        parsed = JsonNode.Parse(raw);
    }
    catch (Exception)
    {
        // Not JSON. Deliberately NOT echoed as a raw string: redaction is by KEY, so a
        // form body copied whole into a "echo" field would be logged verbatim — correct
        // per policy, and a confusing thing for a sample to demonstrate.
    }

    logger.LogInformation("bet received {Bytes} bytes", raw.Length);
    logger.Audit("bet.placed", new { actor = "smoke-test", userId = "u_test", bytes = raw.Length });

    return Results.Json(new Dictionary<string, object?>
    {
        ["ok"] = true,
        ["echo"] = parsed,
        ["received_bytes"] = raw.Length,
    });
});

app.MapGet("/error", () => Results.Json(new { error = "smoke_error" }, statusCode: 500));

app.MapGet("/slow", async () =>
{
    await Task.Delay(1500);
    return Results.Json(new { slow = true });
});

app.MapGet("/crash", IResult () => throw new InvalidOperationException("smoke: unhandled exception"));

app.Run();

# smoke-dotnet

Minimal ASP.NET Core app used to exercise `Beehive.Telemetry` end to end. It is the .NET
twin of `examples/smoke`.

The only telemetry code in `Program.cs` is one line:

```csharp
builder.AddBeehiveTelemetry();
```

Everything else — the `http.access` line per request, the JSON app-log format, the audit
level, the crash handlers — follows from it. There is no `app.UseSomething()` for the
access log.

## Routes

| Route | Behaviour |
| --- | --- |
| `GET /health` | Returns `ok`. On the default ignore list: no access line, no span. |
| `GET /hello` | Returns JSON. Query params exercise per-key query redaction. |
| `POST /bets` | Echoes a JSON request body back as JSON; also emits one `ILogger` info line and one `Audit` event. |
| `GET /error` | Returns 500. |
| `GET /slow` | Waits 1.5s — the slow tier of `HTTP_LOG_PAYLOAD=errors`, and the easiest way to test a client abort. |
| `GET /crash` | Throws. One access line at status 500, then the exception propagates. |

## Run

```sh
ASPNETCORE_URLS=http://localhost:42101 dotnet run
```

Add `ASPNETCORE_ENVIRONMENT=Production` to see the JSON log format (locally it
pretty-prints). Tracing stays off until you point it at a collector:

```sh
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS=http://localhost:42101 \
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 \
dotnet run
```

The project uses a `ProjectReference` to the local package source so it always exercises
the working tree. A real app would use:

```xml
<PackageReference Include="Beehive.Telemetry" Version="0.1.0" />
```

## Things worth trying

```sh
B=http://localhost:42101

# credentials redacted, business data verbatim, Cookie/Authorization never logged
curl -s -o /dev/null -X POST $B/bets -H 'content-type: application/json' \
  -H 'cookie: sid=secret' -H 'authorization: Bearer tok-1' \
  -d '{"password":"pw-1","card_no":"4242424242424242","amount":250}'

# per-key query redaction
curl -s -o /dev/null "$B/hello?sessionid=ss-1&ref=4242424242424242&page=2"

# a client that goes away -> status 499, aborted:true
curl -s -o /dev/null --max-time 0.3 $B/slow

# payload only on failures and slow requests
HTTP_LOG_PAYLOAD=errors dotnet run

# the 100% record is not gated by LOG_LEVEL, and audit survives it
LOG_LEVEL=error dotnet run
```

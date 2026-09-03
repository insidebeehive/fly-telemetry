using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Beehive.Telemetry.Http;

/// <summary>
/// Emits exactly ONE <c>http.access</c> JSON line per request, straight to stdout.
/// </summary>
/// <remarks>
/// <para>
/// The line is the 100% "which request went where and responded how" record: method,
/// path/route, url, host, status, duration, ip, trace ids — for every request, including
/// ones that threw and ones the client abandoned. When the <c>HTTP_LOG_PAYLOAD</c> policy
/// fires, the SAME line additionally carries redacted headers and capped bodies, so a failed
/// transaction's record is self-contained evidence and "all requests" queries never have to
/// deal with a second message type.
/// </para>
/// <para>
/// Every line carries <c>logger=http</c>, the stream discriminator that keeps this firehose
/// queryable separately from ordinary app logs. Lines bypass <c>ILogger</c> entirely, so
/// <c>LOG_LEVEL</c> cannot silence them and the package's own console formatter cannot recurse.
/// </para>
/// </remarks>
internal sealed class HttpAccessLogMiddleware
{
    private static readonly string[] ResponseHeaderAllowList = ["content-type", "content-length", "content-encoding"];

    private readonly RequestDelegate next;
    private readonly HttpLogOptions options;

    public HttpAccessLogMiddleware(RequestDelegate next, HttpLogOptions options)
    {
        this.next = next;
        this.options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string path;
        try
        {
            path = (context.Request.PathBase.Value ?? string.Empty) + (context.Request.Path.Value ?? string.Empty);
            if (path.Length == 0)
            {
                path = "/";
            }
        }
        catch (Exception)
        {
            path = "/";
        }

        if (options.IsIgnored(path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var wantPayload = options.PayloadMode != PayloadMode.Off;

        // --- request body: observe-only, rewound so model binding still works -----
        byte[] requestBody = [];
        long requestTotal = 0;
        if (wantPayload && !HttpLogOptions.IsBodyless(context.Request.Method))
        {
            try
            {
                (requestBody, requestTotal) = await CaptureRequestBodyAsync(context).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Observe-only — a capture failure must never touch the request.
            }
        }

        // --- response body: pass-through counting stream ---------------------------
        var originalBodyFeature = context.Features.Get<IHttpResponseBodyFeature>();
        var originalBody = context.Response.Body;
        ResponseCaptureStream? capture = null;
        try
        {
            capture = new ResponseCaptureStream(originalBody, wantPayload ? options.BodyMax : 0);
            context.Response.Body = capture;
        }
        catch (Exception)
        {
            capture = null;
        }

        Exception? failure = null;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            if (capture is not null)
            {
                try
                {
                    // Restoring the FEATURE (not just the stream) hands the server back the
                    // exact object it started with, including its own completion semantics.
                    if (originalBodyFeature is not null)
                    {
                        context.Features.Set(originalBodyFeature);
                    }
                    else
                    {
                        context.Response.Body = originalBody;
                    }
                }
                catch (Exception)
                {
                    // Nothing sensible to do; the request is over either way.
                }
            }
        }

        try
        {
            Emit(context, path, start, capture, requestBody, requestTotal, wantPayload, failure);
        }
        catch (Exception error)
        {
            WarnOnce(error);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void Emit(
        HttpContext context,
        string path,
        long start,
        ResponseCaptureStream? capture,
        byte[] requestBody,
        long requestTotal,
        bool wantPayload,
        Exception? failure)
    {
        var request = context.Request;
        var response = context.Response;
        var durationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        var clientGone = context.RequestAborted.IsCancellationRequested;
        var abortedFailure = failure is OperationCanceledException && clientGone;

        int status;
        var aborted = false;
        if (failure is BadHttpRequestException badRequest && !response.HasStarted)
        {
            // Kestrel's own accounting for a request the CLIENT broke (cut mid-upload ->
            // "Unexpected end of request content" = 400, oversized body = 413, ...) — a
            // client fault must not inflate this service's 500 rate (found by blind QA).
            status = badRequest.StatusCode;
            aborted = clientGone;
        }
        else if (failure is not null && !abortedFailure)
        {
            // The framework never got to write a status; 500 is what the client will see.
            status = response.HasStarted ? response.StatusCode : 500;
        }
        else if (clientGone)
        {
            status = 499; // nginx convention: client closed request
            aborted = true;
        }
        else
        {
            status = response.StatusCode;
        }

        var record = new JsonObject
        {
            ["level"] = "info",
            ["message"] = "http.access",
            ["ts"] = RawLog.Timestamp(),
            ["logger"] = "http",
            ["service"] = options.Service,
            ["method"] = request.Method,
            ["path"] = path,
        };

        var route = ResolveRoute(context);
        if (route is not null)
        {
            record["route"] = route;
        }

        record["url"] = Redaction.RedactUrl((request.Path.Value ?? string.Empty) + (request.QueryString.Value ?? string.Empty));

        // Host HEADER — named http_host because a plain `host` field collides with the
        // machine-host stream field of hosted log pipelines, and the Host header is
        // client-controlled (unbounded stream cardinality).
        var httpHost = Header(request, "host");
        if (httpHost is not null)
        {
            record["http_host"] = httpHost;
        }

        record["status"] = status;
        record["duration_ms"] = Math.Round(durationMs, 1, MidpointRounding.AwayFromZero);

        var ip = ResolveIp(context);
        if (ip is not null)
        {
            record["ip"] = ip;
        }

        if (aborted)
        {
            record["aborted"] = true;
        }

        // trace_sampled says whether this trace_id will resolve to stored spans — head
        // sampling keeps a fraction, so this saves chasing ids that were never exported.
        if (RawLog.TryActivityIds(out var traceId, out var spanId, out var sampled)
            || RawLog.TryHeaderIds(Header(request, "traceparent"), out traceId, out spanId, out sampled))
        {
            record["trace_id"] = traceId;
            record["span_id"] = spanId;
            record["trace_sampled"] = sampled;
        }

        var responseTotal = capture?.Total ?? 0;
        if (responseTotal > 0)
        {
            record["res_bytes"] = responseTotal;
        }

        if (wantPayload && options.ShouldEnrich(path, status, durationMs))
        {
            Enrich(context, record, capture, requestBody, requestTotal, responseTotal);
        }

        RawLog.Write(record);
    }

    private void Enrich(
        HttpContext context,
        JsonObject record,
        ResponseCaptureStream? capture,
        byte[] requestBody,
        long requestTotal,
        long responseTotal)
    {
        var request = context.Request;
        var response = context.Response;

        // Stable selector for enriched lines in either body mode.
        record["payload"] = true;
        record["req_headers"] = HeadersObject(Redaction.PickHeaders(name => Header(request, name)));

        var requestRendered = BodyRenderer.Render(
            requestBody,
            requestTotal,
            Header(request, "content-type"),
            Header(request, "content-encoding"),
            jsonOnly: false,
            options.BodyMode);

        if (requestRendered is null)
        {
            // Bodies deliberately never read (compressed, multipart, other binary) still get
            // size evidence, so an enriched line is never silently body-less.
            var placeholder = BodyRenderer.RequestPlaceholder(
                request.Method,
                Header(request, "content-type"),
                Header(request, "content-encoding"),
                Header(request, "content-length"));
            if (placeholder is not null)
            {
                requestRendered = JsonValue.Create(placeholder);
            }
        }

        if (requestRendered is not null)
        {
            record["req_body"] = requestRendered;
        }

        if (requestTotal > options.BodyMax)
        {
            record["req_body_truncated"] = true;
        }

        List<KeyValuePair<string, string>> responseHeaders;
        try
        {
            responseHeaders = Redaction.PickHeaders(name => ResponseHeader(response, name), ResponseHeaderAllowList);
        }
        catch (Exception)
        {
            responseHeaders = [];
        }

        record["res_headers"] = HeadersObject(responseHeaders);

        if (capture is not null)
        {
            var responseRendered = BodyRenderer.Render(
                capture.Captured,
                responseTotal,
                ResponseHeader(response, "content-type"),
                ResponseHeader(response, "content-encoding"),
                jsonOnly: true,
                options.BodyMode);

            if (responseRendered is not null)
            {
                record["res_body"] = responseRendered;
            }
        }

        if (responseTotal > options.BodyMax)
        {
            record["res_body_truncated"] = true;
        }
    }

    private async Task<(byte[] Body, long Total)> CaptureRequestBodyAsync(HttpContext context)
    {
        var request = context.Request;
        if (!BodyRenderer.IsCapturableRequest(Header(request, "content-type"), Header(request, "content-encoding")))
        {
            return ([], 0);
        }

        // Buffering is what lets MVC / minimal-API model binding read the same bytes after
        // us; the position is rewound below so nothing downstream notices.
        request.EnableBuffering();

        var max = options.BodyMax;
        var buffer = new byte[max + 1]; // the +1 is what detects truncation
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await request.Body.ReadAsync(buffer.AsMemory(read, buffer.Length - read), CancellationToken.None).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        // Under the cap the read count IS the total; over it, content-length is the only
        // honest source (the rest of the stream was deliberately not drained).
        var total = read <= max ? read : Math.Max(request.ContentLength ?? read, read);
        return (buffer[..Math.Min(read, max)], total);
    }

    private static JsonObject HeadersObject(List<KeyValuePair<string, string>> headers)
    {
        var obj = new JsonObject();
        foreach (var header in headers)
        {
            obj[header.Key] = header.Value;
        }

        return obj;
    }

    private static string? Header(HttpRequest request, string name)
    {
        try
        {
            return request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResponseHeader(HttpResponse response, string name)
    {
        try
        {
            return response.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Endpoint routing has resolved by the time the inner pipeline returns, so the route
    /// template — which is what keeps path cardinality sane in queries — is available here
    /// even though it was not on the way in.
    /// </summary>
    private static string? ResolveRoute(HttpContext context)
    {
        try
        {
            if (context.GetEndpoint() is RouteEndpoint routeEndpoint)
            {
                var text = routeEndpoint.RoutePattern.RawText;
                return string.IsNullOrEmpty(text) ? null : text;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResolveIp(HttpContext context)
    {
        try
        {
            var forwarded = Header(context.Request, "x-forwarded-for");
            if (!string.IsNullOrEmpty(forwarded))
            {
                var cut = forwarded.IndexOf(',');
                var first = (cut < 0 ? forwarded : forwarded[..cut]).Trim();
                if (first.Length > 0)
                {
                    return first;
                }
            }

            var remote = context.Connection.RemoteIpAddress?.ToString();
            return string.IsNullOrEmpty(remote) ? null : remote;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int warned;

    private static void WarnOnce(Exception error)
    {
        if (Interlocked.Exchange(ref warned, 1) == 1)
        {
            return;
        }

        TelemetryEnv.Warn("http logger error (reported once)", error);
    }
}

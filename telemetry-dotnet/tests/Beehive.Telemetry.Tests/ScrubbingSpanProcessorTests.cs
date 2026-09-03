using System.Diagnostics;
using Beehive.Telemetry.Tracing;
using Xunit;

namespace Beehive.Telemetry.Tests;

/// <summary>
/// The structural backstop: whatever the instrumentation put on a span, credentials do not
/// leave the process. Configuration is the primary guarantee; this is what survives a
/// dependency bump that changes an upstream default.
/// </summary>
public class ScrubbingSpanProcessorTests
{
    private static Activity Scrub(params (string Key, object? Value)[] tags)
    {
        var activity = new Activity("test-span");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        foreach (var (key, value) in tags)
        {
            activity.SetTag(key, value);
        }

        activity.Stop();

        new ScrubbingSpanProcessor().OnEnd(activity);
        return activity;
    }

    private static string? Tag(Activity activity, string key) => activity.GetTagItem(key) as string;

    [Fact]
    public void CapturedRequestAndResponseHeadersAreDeleted()
    {
        var activity = Scrub(
            ("http.request.header.authorization", "Bearer tok-1"),
            ("http.response.header.set-cookie", "sid=1"),
            ("http.request.method", "GET"));

        Assert.Null(activity.GetTagItem("http.request.header.authorization"));
        Assert.Null(activity.GetTagItem("http.response.header.set-cookie"));
        Assert.Equal("GET", Tag(activity, "http.request.method"));
    }

    [Fact]
    public void SensitivelyNamedAttributesAreRedacted()
    {
        var activity = Scrub(
            ("session_token", "st-1"),
            ("app.apiKey", "ak-1"),
            ("db.statement", "select 1"),
            ("order.amount", 250));

        Assert.Equal("[REDACTED]", Tag(activity, "session_token"));
        Assert.Equal("[REDACTED]", Tag(activity, "app.apiKey"));
        Assert.Equal("select 1", Tag(activity, "db.statement"));
        Assert.Equal(250, activity.GetTagItem("order.amount"));
    }

    [Theory]
    [InlineData("url.full")]
    [InlineData("http.url")]
    [InlineData("http.target")]
    [InlineData("url.path")]
    public void UrlAttributesGetPerKeyQueryRedaction(string key)
    {
        var activity = Scrub((key, "https://api.example.com/orders?token=tk-1&page=2"));

        Assert.Equal("https://api.example.com/orders?token=[REDACTED]&page=2", Tag(activity, key));
    }

    [Fact]
    public void BareQueryAttributesGetPerKeyRedaction()
    {
        var activity = Scrub(("url.query", "apikey=ak-1&page=2&ref=4242424242424242"));

        // Evidence-first: only the access-granting param goes.
        Assert.Equal("apikey=[REDACTED]&page=2&ref=4242424242424242", Tag(activity, "url.query"));
    }

    [Fact]
    public void NonStringUrlAttributesAreLeftAlone()
    {
        var activity = Scrub(("url.full", 42));

        Assert.Equal(42, activity.GetTagItem("url.full"));
    }

    [Fact]
    public void AnActivityWithNoTagsIsHandled()
    {
        var activity = Scrub();

        Assert.Empty(activity.TagObjects);
    }
}

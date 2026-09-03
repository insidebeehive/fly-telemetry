using System.Text;
using Beehive.Telemetry.Http;
using Xunit;

namespace Beehive.Telemetry.Tests;

/// <summary>
/// Which bodies become log text, which become a size placeholder, and what the placeholder
/// says. Bytes that cannot be scrubbed safely must never be rendered.
/// </summary>
public class BodyRendererTests
{
    private static string Render(string body, string? contentType, string? contentEncoding = null, bool jsonOnly = false, BodyMode mode = BodyMode.String)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var node = BodyRenderer.Render(bytes, bytes.Length, contentType, contentEncoding, jsonOnly, mode);
        return node?.ToString() ?? "<null>";
    }

    [Fact]
    public void EmptyBodyRendersNothing() =>
        Assert.Null(BodyRenderer.Render(default, 0, "application/json", null, false, BodyMode.String));

    [Fact]
    public void CompressedBodiesAreNeverDecoded() =>
        Assert.Equal("[gzip 1234 bytes]", BodyRenderer.Render(default, 1234, "application/json", "gzip", false, BodyMode.String)!.ToString());

    [Fact]
    public void IdentityEncodingIsNotAPlaceholder() =>
        Assert.Equal("""{"a":1}""", Render("""{"a":1}""", "application/json", "identity"));

    [Fact]
    public void NonAsciiCompatibleCharsetsGetAPlaceholder()
    {
        var utf16 = Encoding.Unicode.GetBytes("""{"password":"pw"}""");

        var result = BodyRenderer.Render(utf16, utf16.Length, "application/json; charset=utf-16le", null, false, BodyMode.String);

        Assert.Equal("[application/json utf-16le 34 bytes]", result!.ToString());
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("UTF-8")]
    [InlineData("iso-8859-1")]
    [InlineData("windows-1252")]
    public void AsciiCompatibleCharsetsAreRendered(string charset) =>
        Assert.Equal("""{"a":1}""", Render("""{"a":1}""", "application/json; charset=" + charset));

    [Fact]
    public void ResponsesRenderJsonOnly()
    {
        Assert.Equal("[text/html 10 bytes]", Render("<html></h>", "text/html", jsonOnly: true));
        Assert.Equal("""{"a":1}""", Render("""{"a":1}""", "application/json", jsonOnly: true));
    }

    [Fact]
    public void RequestsAlsoRenderTextAndForms()
    {
        Assert.Equal("hello password: [REDACTED]", Render("hello password: hunter2", "text/plain"));

        // Form bodies are DECODED into a key/value object (not re-encoded): sensitive key
        // redacted, business data verbatim.
        Assert.Equal(
            """{"token":"[REDACTED]","card":"4111111111111111"}""",
            Render("token=tk-1&card=4111111111111111", "application/x-www-form-urlencoded"));
    }

    [Fact]
    public void UrlencodedBodiesDecodeIntoAnObjectLikeJson()
    {
        var result = Render("user=amit+kumar&password=secret&note=hi%20there", "application/x-www-form-urlencoded");

        Assert.Equal("""{"user":"amit kumar","password":"[REDACTED]","note":"hi there"}""", result);
    }

    [Fact]
    public void UrlencodedBodiesRenderIdenticallyToTheEquivalentJsonBody()
    {
        var form = Render("user=amit+kumar&password=secret&note=hi%20there", "application/x-www-form-urlencoded");
        var json = Render("""{"user":"amit kumar","password":"secret","note":"hi there"}""", "application/json");

        Assert.Equal(json, form);
    }

    [Fact]
    public void UrlencodedObjectModeLandsAsNestedFields()
    {
        var bytes = Encoding.UTF8.GetBytes("user=amit+kumar&password=secret&amount=250");
        var node = BodyRenderer.Render(bytes, bytes.Length, "application/x-www-form-urlencoded", null, jsonOnly: false, BodyMode.Object);

        Assert.NotNull(node);
        Assert.Equal("amit kumar", node!["user"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", node["password"]!.GetValue<string>());
        Assert.Equal("250", node["amount"]!.GetValue<string>());
    }

    [Fact]
    public void UrlencodedRepeatedKeysCollapseToAnArray()
    {
        Assert.Equal("""{"a":["1","2"]}""", Render("a=1&a=2", "application/x-www-form-urlencoded"));
    }

    [Fact]
    public void BinaryRequestBodiesGetAPlaceholder() =>
        Assert.Equal("[image/png 8 bytes]", Render("PNG-data", "image/png"));

    [Fact]
    public void ObjectModeKeepsTheParsedStructure()
    {
        var node = BodyRenderer.Render(
            Encoding.UTF8.GetBytes("""{"token":"t","amount":250}"""),
            26,
            "application/json",
            null,
            jsonOnly: false,
            BodyMode.Object);

        Assert.NotNull(node);
        Assert.Equal("[REDACTED]", node!["token"]!.GetValue<string>());
        Assert.Equal(250, node["amount"]!.GetValue<int>());
    }

    [Fact]
    public void StringModeRendersOneJsonEncodedString()
    {
        var result = Render("""{"token":"t","amount":250}""", "application/json");

        Assert.Equal("""{"token":"[REDACTED]","amount":250}""", result);
    }

    [Fact]
    public void UnparseableJsonFallsBackToTheTextScrubberNeverToRawText()
    {
        var result = Render("""{"session_token":"eyJhbGciOi""", "application/json");

        Assert.DoesNotContain("eyJhbGciOi", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }

    // --- request placeholders (bodies deliberately never read) -----------------
    [Theory]
    [InlineData("POST", "application/json", "gzip", "1234", "[gzip 1234 bytes]")]
    [InlineData("POST", "multipart/form-data; boundary=x", null, "1234", "[multipart/form-data 1234 bytes]")]
    [InlineData("POST", "image/png", null, null, "[image/png unknown size]")]
    [InlineData("POST", "image/png", null, "not-a-number", "[image/png unknown size]")]
    [InlineData("POST", "image/png", null, "12345678901234567", "[image/png unknown size]")]
    public void RequestPlaceholdersCarrySizeEvidence(string method, string contentType, string? encoding, string? length, string expected) =>
        Assert.Equal(expected, BodyRenderer.RequestPlaceholder(method, contentType, encoding, length));

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("DELETE")]
    public void BodylessMethodsGetNoPlaceholder(string method) =>
        Assert.Null(BodyRenderer.RequestPlaceholder(method, "image/png", null, "10"));

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData(null)]
    public void CapturedContentTypesGetNoPlaceholder(string? contentType) =>
        Assert.Null(BodyRenderer.RequestPlaceholder("POST", contentType, null, "10"));

    [Theory]
    [InlineData("application/json", null, true)]
    [InlineData("text/plain; charset=utf-8", null, true)]
    [InlineData("application/x-www-form-urlencoded", null, true)]
    [InlineData("application/json", "gzip", false)]
    [InlineData("image/png", null, false)]
    [InlineData("", null, false)]
    public void OnlyTextualUncompressedRequestsAreTapped(string contentType, string? encoding, bool expected) =>
        Assert.Equal(expected, BodyRenderer.IsCapturableRequest(contentType, encoding));
}

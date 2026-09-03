using System.Text.Json.Nodes;
using Xunit;

namespace Beehive.Telemetry.Tests;

/// <summary>
/// The policy battery: what must be redacted, and — just as load-bearing — what must NOT be.
/// Logs are evidence; over-redaction of business data is a defect, not a safe default.
/// </summary>
public class RedactionPolicyTests
{
    // --- 1..13 credentials are redacted, in every key spelling -----------------
    [Theory]
    [InlineData("password")]
    [InlineData("passwd")]
    [InlineData("pwd")]
    [InlineData("Password")]
    [InlineData("user_password")]
    [InlineData("otp")]
    [InlineData("mpin")]
    [InlineData("session")]
    [InlineData("sessionid")]
    [InlineData("session_key")]
    [InlineData("jsessionid")]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("refreshToken")]
    [InlineData("secret")]
    [InlineData("apikey")]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("x-api-key")]
    [InlineData("X-API-KEY")]
    [InlineData("privatekey")]
    [InlineData("private_key")]
    [InlineData("accesskey")]
    [InlineData("credential")]
    [InlineData("credentials")]
    [InlineData("authorization")]
    [InlineData("cookie")]
    [InlineData("signature")]
    [InlineData("hashkey")]
    [InlineData("hash_key")]
    [InlineData("saltkey")]
    [InlineData("salt_key")]
    public void SensitiveKeysAreMatched(string key) => Assert.True(Redaction.IsSensitiveKey(key));

    // --- 14..15 whole-word-only keys ------------------------------------------
    [Theory]
    [InlineData("pin")]
    [InlineData("PIN")]
    [InlineData("p-i-n")]
    [InlineData("sig")]
    [InlineData("SIG")]
    public void ExactSensitiveKeysAreMatched(string key) => Assert.True(Redaction.IsSensitiveKey(key));

    // --- 16..21 benign keys must NOT be matched (over-redaction is a defect) ---
    [Theory]
    [InlineData("spinCount")]
    [InlineData("spin_count")]
    [InlineData("cacheKey")]
    [InlineData("design")]
    [InlineData("keyword")]
    [InlineData("designation")]
    [InlineData("pinned")]
    [InlineData("amount")]
    [InlineData("card_no")]
    [InlineData("bank_ref")]
    [InlineData("orderId")]
    [InlineData("userId")]
    [InlineData("key")]
    public void BenignKeysAreNotMatched(string key) => Assert.False(Redaction.IsSensitiveKey(key));

    // --- 22 JSON: credentials go, business data stays verbatim -----------------
    [Fact]
    public void JsonRedactsCredentialsAndKeepsBusinessDataVerbatim()
    {
        const string body = """
            {"password":"pw-1","session_key":"sk-1","card_no":"4242424242424242","amount":250,"bank_ref":"40741852963074181"}
            """;

        var result = Redaction.RedactJsonText(body);

        Assert.NotNull(result);
        Assert.Contains("\"password\":\"[REDACTED]\"", result, StringComparison.Ordinal);
        Assert.Contains("\"session_key\":\"[REDACTED]\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("pw-1", result, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-1", result, StringComparison.Ordinal);

        // Evidence-first: card numbers, amounts and refs are NOT touched.
        Assert.Contains("\"card_no\":\"4242424242424242\"", result, StringComparison.Ordinal);
        Assert.Contains("\"amount\":250", result, StringComparison.Ordinal);
        Assert.Contains("\"bank_ref\":\"40741852963074181\"", result, StringComparison.Ordinal);
    }

    // --- 23 nested structures keep their shape ---------------------------------
    [Fact]
    public void JsonRedactsNestedAndArrayValues()
    {
        const string body = """
            {"user":{"pin":"1234","name":"ada"},"items":[{"token":"t1","sku":"A-9"},{"price":10}]}
            """;

        var result = Redaction.RedactJsonText(body);

        Assert.NotNull(result);
        Assert.Contains("\"pin\":\"[REDACTED]\"", result, StringComparison.Ordinal);
        Assert.Contains("\"token\":\"[REDACTED]\"", result, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"ada\"", result, StringComparison.Ordinal);
        Assert.Contains("\"sku\":\"A-9\"", result, StringComparison.Ordinal);
        Assert.Contains("\"price\":10", result, StringComparison.Ordinal);
    }

    // --- 24 recursion floor ----------------------------------------------------
    [Fact]
    public void JsonTruncatesBeyondTheDepthCap()
    {
        const string body = """{"a":{"b":{"c":{"d":{"e":{"f":1}}}}}}""";

        var result = Redaction.RedactJsonText(body);

        Assert.Equal("""{"a":{"b":{"c":{"d":{"e":{"f":"[TRUNCATED]"}}}}}}""", result);
    }

    // --- 25 a whole sensitive subtree collapses to one marker ------------------
    [Fact]
    public void JsonRedactsWholeSubtreeUnderASensitiveKey()
    {
        var result = Redaction.RedactJsonText("""{"credentials":{"user":"u","pass":"p"},"id":7}""");

        Assert.Equal("""{"credentials":"[REDACTED]","id":7}""", result);
    }

    // --- 26..28 query strings ---------------------------------------------------
    [Fact]
    public void QueryRedactsByKeyAndKeepsThePathVerbatim()
    {
        var result = Redaction.RedactUrl("/hello?sessionid=ss-1&ref=4242424242424242&page=2");

        Assert.Equal("/hello?sessionid=[REDACTED]&ref=4242424242424242&page=2", result);
    }

    [Fact]
    public void QueryKeysAreUrlDecodedBeforeTheKeyTest()
    {
        // x%2Dapi%2Dkey is "x-api-key" — it must not evade the test by being encoded.
        var result = Redaction.RedactUrl("/cb?x%2Dapi%2Dkey=ak-1&page=2");

        Assert.DoesNotContain("ak-1", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
        Assert.Contains("page=2", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/plain/path", "/plain/path")]
    [InlineData("/p?", "/p?")]
    [InlineData("/a/b/c/4242424242424242", "/a/b/c/4242424242424242")]
    public void UrlsWithoutAQueryPassThroughVerbatim(string input, string expected) =>
        Assert.Equal(expected, Redaction.RedactUrl(input));

    [Fact]
    public void StripQueryDropsEverythingAfterTheQuestionMark() =>
        Assert.Equal("/cb?[REDACTED]", Redaction.StripQuery("/cb?token=x&sig=y"));

    // --- 29 urlencoded form bodies ---------------------------------------------
    [Fact]
    public void UrlEncodedFormRedactsByKey()
    {
        var result = Redaction.RedactQueryString("token=tk-1&card=4111111111111111&note=hi");

        Assert.Equal("token=[REDACTED]&card=4111111111111111&note=hi", result);
    }

    // --- 30..34 ScrubText: the fallback for text that would not parse ----------
    [Fact]
    public void ScrubTextRedactsAValueSlicedByTheByteCap()
    {
        // A body cut mid-value by HTTP_LOG_BODY_MAX: JSON.parse fails, the key still tells
        // us the value must go.
        const string sliced = """{"amount":250,"session_token":"eyJhbGciOiJIUzI1NiIsInR5cCI6Ikp""";

        var result = Redaction.ScrubText(sliced);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6Ikp", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
        Assert.Contains("\"amount\":250", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubTextRedactsSingleQuotedMalformedJson()
    {
        var result = Redaction.ScrubText("{'password':'hunter2','card':'4242424242424242'}");

        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.Contains("4242424242424242", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubTextSeesThroughNulInterleavedUtf16Text()
    {
        // utf-16 bytes behind a lying utf-8 charset: every key name is NUL-interleaved and
        // would otherwise slip past every regex.
        var interleaved = string.Concat("{\"password\":\"hunter2\"}".Select(c => c + "\0"));

        var result = Redaction.ScrubText(interleaved);

        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password: hunter2", "hunter2")]
    [InlineData("some log line token=abc123 more", "abc123")]
    [InlineData("{ apikey: ak-99, page: 2 }", "ak-99")]
    [InlineData("a=1&otp=999999", "999999")]
    public void ScrubTextRedactsBarewordAndUrlencodedShapes(string input, string secret)
    {
        var result = Redaction.ScrubText(input);

        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubTextRedactsArrayValuesUnderASensitiveKey()
    {
        var result = Redaction.ScrubText("""{"tokens":["a","b"],"ids":[1,2]}""");

        Assert.DoesNotContain("\"a\"", result, StringComparison.Ordinal);
        Assert.Contains("[1,2]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubTextRedactsBarewordJsonValues()
    {
        var result = Redaction.ScrubText("""{"otp":123456,"amount":250}""");

        Assert.DoesNotContain("123456", result, StringComparison.Ordinal);
        Assert.Contains("250", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubTextKeepsBusinessDataVerbatim()
    {
        const string text = "order o_991 card 4242424242424242 amount 250 ref 40741852963074181";

        Assert.Equal(text, Redaction.ScrubText(text));
    }

    [Fact]
    public void ScrubTextStripsNulBytes() => Assert.Equal("ab", Redaction.ScrubText("a\0b"));

    // --- 35..37 headers ---------------------------------------------------------
    [Fact]
    public void HeaderAllowListNeverPicksCookieOrAuthorization()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "api.example.com",
            ["cookie"] = "sid=secret-cookie-value",
            ["authorization"] = "Bearer tok-abc123",
            ["x-api-key"] = "ak-1",
            ["user-agent"] = "curl/8",
            ["content-type"] = "application/json",
        };

        var picked = Redaction.PickHeaders(headers);
        var keys = picked.Select(pair => pair.Key).ToList();

        Assert.DoesNotContain("cookie", keys, StringComparer.Ordinal);
        Assert.DoesNotContain("authorization", keys, StringComparer.Ordinal);
        Assert.DoesNotContain("x-api-key", keys, StringComparer.Ordinal);
        Assert.Contains("host", keys, StringComparer.Ordinal);
        Assert.Contains("user-agent", keys, StringComparer.Ordinal);
    }

    [Fact]
    public void HeadersComeBackInAllowListOrder()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["user-agent"] = "curl/8",
            ["content-type"] = "application/json",
            ["host"] = "api.example.com",
        };

        var keys = Redaction.PickHeaders(headers).Select(pair => pair.Key).ToArray();

        Assert.Equal(new[] { "host", "content-type", "user-agent" }, keys);
    }

    [Fact]
    public void RefererQueryIsRedactedLikeAnyOtherUrl()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["referer"] = "https://a/checkout?apikey=ak-1&step=2",
        };

        var value = Redaction.PickHeaders(headers).Single().Value;

        Assert.Equal("https://a/checkout?apikey=[REDACTED]&step=2", value);
    }

    [Fact]
    public void HeaderValuesAreCappedWithACountSuffix()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["user-agent"] = new string('u', 600),
        };

        var value = Redaction.PickHeaders(headers).Single().Value;

        Assert.StartsWith(new string('u', 512), value, StringComparison.Ordinal);
        Assert.EndsWith("…[+88 chars]", value, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyHeaderValuesAreStillPicked()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["origin"] = string.Empty };

        Assert.Equal(string.Empty, Redaction.PickHeaders(headers).Single().Value);
    }

    // --- span attribute policy --------------------------------------------------
    [Fact]
    public void JsonNullsSurviveAsNulls()
    {
        var node = Redaction.RedactJson(JsonNode.Parse("""{"a":null,"token":null}"""));

        Assert.Equal("""{"a":null,"token":"[REDACTED]"}""", Redaction.ToJsonString(node));
    }

    [Fact]
    public void NonJsonTextIsReportedAsUnparseable() => Assert.Null(Redaction.RedactJsonText("not json at all {"));
}

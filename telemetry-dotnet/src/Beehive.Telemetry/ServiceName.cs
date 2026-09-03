using System.Reflection;

namespace Beehive.Telemetry;

/// <summary>
/// service.name resolution, shared by tracing, the http logger and the app logger so spans
/// and log lines always agree:
/// <c>OTEL_SERVICE_NAME &gt; FLY_APP_NAME &gt; entry assembly name &gt; "unknown-service"</c>.
/// </summary>
internal static class ServiceName
{
    private static string? cached;

    internal static string Resolve()
    {
        var value = cached;
        if (value is not null)
        {
            return value;
        }

        value = TelemetryEnv.Raw("OTEL_SERVICE_NAME")
            ?? TelemetryEnv.Raw("FLY_APP_NAME")
            ?? EntryAssemblyName()
            ?? "unknown-service";

        cached = value;
        return value;
    }

    private static string? EntryAssemblyName()
    {
        try
        {
            var name = Assembly.GetEntryAssembly()?.GetName().Name;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Test hook: drops the memoised value so a changed environment is re-read.</summary>
    internal static void ResetForTests() => cached = null;
}

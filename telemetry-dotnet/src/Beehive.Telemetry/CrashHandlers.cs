using System.Text.Json.Nodes;
using Beehive.Telemetry.Logging;

namespace Beehive.Telemetry;

/// <summary>
/// Turns a fatal crash into ONE structured line instead of a raw multi-line stack, which a
/// line-based log pipeline would otherwise split into N unqueryable records.
/// </summary>
/// <remarks>
/// The line goes straight to stdout: mid-crash the logging pipeline may already be tearing
/// down. Nothing here swallows the failure — the process still dies exactly as it would
/// have, so the platform restarts it.
/// </remarks>
internal static class CrashHandlers
{
    private static int installed;

    internal static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        // Log, then let the runtime finish terminating: crash-only by design.
        Emit(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        // Deliberately NOT calling args.SetObserved(): the failure stays unobserved, exactly
        // as it would be without this package.
        Emit(args.Exception, "TaskScheduler.UnobservedTaskException");
    }

    private static void Emit(Exception? error, string source)
    {
        try
        {
            var record = new JsonObject
            {
                ["level"] = "error",
                ["message"] = error?.Message ?? source,
                ["timestamp"] = RawLog.Timestamp(),
                ["logger"] = "app",
                ["service"] = ServiceName.Resolve(),
                ["source"] = source,
            };

            if (error is not null)
            {
                record["err"] = LogRecordBuilder.Error(error);
            }

            RawLog.Write(record);
        }
        catch (Exception)
        {
            // Already crashing; there is nothing better to try.
        }
    }
}

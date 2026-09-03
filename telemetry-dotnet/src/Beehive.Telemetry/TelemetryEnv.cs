using System.Globalization;

namespace Beehive.Telemetry;

/// <summary>
/// Environment reading with the package's house rules: an unset OR empty variable is
/// "not configured", and an INVALID value falls back LOUDLY to the safe default. A typo
/// must never silently disable evidence capture.
/// </summary>
internal static class TelemetryEnv
{
    /// <summary>Reads a variable, treating empty exactly like unset (JS parity).</summary>
    internal static string? Raw(string name)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string Get(string name, string fallback) => Raw(name) ?? fallback;

    /// <summary>Reads a case-insensitive enumerated value, warning and defaulting on anything else.</summary>
    internal static string Choice(string name, string fallback, params string[] valid)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            return fallback;
        }

        var value = raw.ToLowerInvariant();
        foreach (var candidate in valid)
        {
            if (string.Equals(value, candidate, StringComparison.Ordinal))
            {
                return value;
            }
        }

        Warn($"invalid {name} \"{raw}\" — using \"{fallback}\" (valid: {string.Join("|", valid)})");
        return fallback;
    }

    /// <summary>Reads a numeric value, warning and defaulting when it is not a finite number in range.</summary>
    internal static double Number(string name, double fallback, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            return fallback;
        }

        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed)
            && parsed >= min && parsed <= max)
        {
            return parsed;
        }

        Warn($"invalid {name} \"{raw}\" — using {fallback.ToString(CultureInfo.InvariantCulture)}");
        return fallback;
    }

    /// <summary>Splits a comma-separated list, trimming entries and dropping empties.</summary>
    internal static string[] List(string name, string fallback)
    {
        var raw = Get(name, fallback);
        var parts = raw.Split(',');
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Same match semantics everywhere (http logger and tracer): exact match unless the
    /// entry ends in "/" (subtree prefix); bare "/" stays exact or it would match everything.
    /// </summary>
    internal static bool IsIgnoredPath(string path, IReadOnlyList<string> ignore)
    {
        for (var i = 0; i < ignore.Count; i++)
        {
            var entry = ignore[i];
            if (entry.Length > 1 && entry[^1] == '/')
            {
                if (path.StartsWith(entry, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (string.Equals(path, entry, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Startup banner / notice on stdout, mirroring the JS package's console.log lines.</summary>
    internal static void Info(string message)
    {
        try
        {
            Console.Out.WriteLine("[telemetry] " + message);
        }
        catch (Exception)
        {
            // A telemetry notice must never be the reason a service fails.
        }
    }

    /// <summary>Loud fallback notice on stderr, mirroring the JS package's console.warn lines.</summary>
    internal static void Warn(string message)
    {
        try
        {
            Console.Error.WriteLine("[telemetry] " + message);
        }
        catch (Exception)
        {
            // As above.
        }
    }

    /// <summary>Reports a swallowed telemetry failure without ever propagating it.</summary>
    internal static void Warn(string message, Exception error)
    {
        try
        {
            Console.Error.WriteLine("[telemetry] " + message + " " + error);
        }
        catch (Exception)
        {
            // As above.
        }
    }
}

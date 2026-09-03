using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Beehive.Telemetry.Http;

/// <summary>
/// Puts the access-log middleware FIRST in the pipeline without the application calling
/// <c>app.UseSomething()</c>.
/// </summary>
/// <remarks>
/// <see cref="IStartupFilter"/> instances wrap the application's own pipeline
/// configuration, so registering the middleware here runs it ahead of everything the app
/// adds — including exception handling and routing. That is what makes the integration a
/// single line in <c>Program.cs</c>, and it is also what guarantees the timing covers the
/// whole request and that no upstream middleware can swallow a request unlogged.
/// </remarks>
internal sealed class HttpAccessLogStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            try
            {
                app.UseMiddleware<HttpAccessLogMiddleware>();
            }
            catch (Exception error)
            {
                // Observability must never be the reason a service fails to boot.
                TelemetryEnv.Warn("http logger could not be added to the pipeline, continuing without it", error);
            }

            next(app);
        };
    }
}

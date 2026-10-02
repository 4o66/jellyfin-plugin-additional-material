using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>
/// Makes the server always send a fresh web client index.html. File Transformation rewrites that
/// page as it is served, but the static-file handler still answers "304 Not Modified" from the file
/// on disk, whose date never changes. Browsers then keep an old copy, with an old script address.
/// Dropping the conditional headers for that one small, no-cache page fixes it.
/// </summary>
public sealed class FreshIndexStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    context.Request.Headers.Remove("If-Modified-Since");
                    context.Request.Headers.Remove("If-None-Match");
                }

                await nextMiddleware(context).ConfigureAwait(false);
            });
            next(app);
        };
    }
}

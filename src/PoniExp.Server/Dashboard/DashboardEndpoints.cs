using System.Net;
using Microsoft.AspNetCore.DataProtection;
using PoniExp.Core.Protocol;

namespace PoniExp.Server.Dashboard;

/// <summary>Marks an endpoint as the dashboard's: served only on its port; <see cref="Anonymous"/> ones need no sign-in.</summary>
public sealed record DashboardEndpoint(bool Anonymous = false);

/// <summary>
/// The dashboard (docs/plan.md, Phase 13): Blazor Server pages on their own plain-HTTP port, behind <c>tailscale serve</c>.
/// Dashboard endpoints answer only there and nothing else does, so the relay port shows no dashboard and the dashboard port
/// reaches neither the hub nor the admin API.
/// </summary>
public static class DashboardEndpoints
{
    public static void AddDashboard(this IServiceCollection services)
    {
        services.AddSingleton<DashboardSessions>();
        services.AddSingleton<DashboardActions>();
        services.AddRazorComponents().AddInteractiveServerComponents();
        // Nothing on disk: antiforgery and component state die with the process, as sign-ins do. A repository in memory
        // (not just the ephemeral provider) keeps the default key manager from writing a key file at startup too.
        services.AddDataProtection();
        services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(o => o.XmlRepository = new MemoryXmlRepository());
    }

    private sealed class MemoryXmlRepository : Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository
    {
        private readonly List<System.Xml.Linq.XElement> _elements = [];

        public IReadOnlyCollection<System.Xml.Linq.XElement> GetAllElements()
        {
            lock (_elements)
            {
                return [.. _elements.Select(e => new System.Xml.Linq.XElement(e))];
            }
        }

        public void StoreElement(System.Xml.Linq.XElement element, string friendlyName)
        {
            lock (_elements)
            {
                _elements.Add(new System.Xml.Linq.XElement(element));
            }
        }
    }

    /// <summary>Before endpoints run: keeps each port to its own endpoints, and dashboard ones to signed-in browsers.</summary>
    public static void UseDashboardGate(this WebApplication app, int? dashboardPort)
    {
        app.Use(async (context, next) =>
        {
            var onDashboardPort = dashboardPort is { } port && context.Connection.LocalPort == port;
            var endpoint = context.GetEndpoint()?.Metadata.GetMetadata<DashboardEndpoint>();
            if (!onDashboardPort)
            {
                if (endpoint is null)
                {
                    await next(context);
                }
                else if (context.Request.Path == "/")
                {
                    await Results.Text("PoniExp router\n").ExecuteAsync(context);
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                }

                return;
            }

            if (endpoint is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var sessions = context.RequestServices.GetRequiredService<DashboardSessions>();
            if (!endpoint.Anonymous && sessions.Validate(context.Request.Cookies[DashboardSessions.Cookie]) is null)
            {
                await SignInPage(StatusCodes.Status401Unauthorized, "Not signed in, or the sign-in has ended.").ExecuteAsync(context);
                return;
            }

            await next(context);
        });
    }

    public static void MapDashboard(this WebApplication app)
    {
        // Now, not at the first sign-in: a link signed before the router started can't be replayed, and one signed after must work.
        app.Services.GetRequiredService<DashboardSessions>();
        var manifest = Path.Combine(AppContext.BaseDirectory, "poniexp.staticwebassets.endpoints.json");
        if (File.Exists(manifest))
        {
            app.MapStaticAssets(manifest).WithMetadata(new DashboardEndpoint(Anonymous: true));
        }

        app.MapGet(DashboardSignIn.Path, (HttpContext http, DashboardSessions sessions, string? id, string? router, string? issued, string? signature) =>
        {
            if (DashboardSignIn.FromQuery(id, router, issued, signature) is not { } signIn)
            {
                return SignInPage(StatusCodes.Status400BadRequest, "That isn't a sign-in link.");
            }

            try
            {
                var (token, session) = sessions.SignIn(signIn);
                http.Response.Cookies.Append(DashboardSessions.Cookie, token, new CookieOptions
                {
                    HttpOnly = true,
                    // Behind tailscale serve it's HTTPS; browsers also keep Secure cookies for http://localhost.
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    Expires = session.ExpiresOn,
                });
                http.Response.Headers.CacheControl = "no-store";
                // Strict cookies aren't sent on the redirect that follows a cross-site navigation (the link opened from a
                // terminal counts), so a same-site page moves on to the dashboard instead of a 302.
                return Results.Content("""
                    <!DOCTYPE html><html><head><meta charset="utf-8"><meta http-equiv="refresh" content="0;url=/"><title>PoniExp</title></head>
                    <body><p>Signed in. <a href="/">Open the dashboard</a>.</p></body></html>
                    """, "text/html");
            }
            catch (RouterException e)
            {
                return SignInPage(StatusCodes.Status403Forbidden, e.Message);
            }
        }).WithMetadata(new DashboardEndpoint(Anonymous: true));

        app.MapPost("/signout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(DashboardSessions.Cookie, new CookieOptions { Path = "/" });
            return SignInPage(StatusCodes.Status200OK, "Signed out.");
        }).WithMetadata(new DashboardEndpoint(Anonymous: true)).DisableAntiforgery();

        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode()
            .WithMetadata(new DashboardEndpoint());
    }

    private static IResult SignInPage(int status, string why) => Results.Content($$"""
        <!DOCTYPE html><html><head><meta charset="utf-8"><title>PoniExp dashboard</title>
        <style>body{font:15px system-ui,sans-serif;margin:3rem;color:#ddd;background:#16181d}code{background:#262a33;padding:.15rem .4rem;border-radius:4px}</style>
        </head><body><h1>PoniExp dashboard</h1><p>{{WebUtility.HtmlEncode(why)}}</p>
        <p>Sign in from a machine whose SSH agent holds the owner key: <code>poniexp-client dashboard</code></p></body></html>
        """, "text/html", statusCode: status);
}

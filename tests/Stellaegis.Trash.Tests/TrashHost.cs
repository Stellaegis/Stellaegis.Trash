using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;
using Stellaegis.Primitives.Hosting;
using Stellaegis.Trash.AspNetCore;

namespace Stellaegis.Trash.Tests;

/// <summary>
/// An in-memory host mapping the trash API twice: under <c>/api/t/{compartment}</c>, as Stellaegis routes its
/// tenants, and under <c>/api/shared</c>; and its administration under <c>/api/admin</c>, for the <c>admin</c> role.
/// Every operation is open to any authenticated caller, and a handler refuses <c>mallory</c> every item.
/// </summary>
public sealed class TrashHost : IAsyncLifetime
{
    private WebApplication? _app;

    private TestServer Server => _app?.GetTestServer() ?? throw new InvalidOperationException("The host is not started.");

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddStellaegisTrashInMemory(builder.Configuration);
        builder.Services.AddStellaegisFrame(ApplicationFrame.ManyCompartments().WithSharedSpace());
        builder.Services.AddStellaegisRequestContext(c => c.CompartmentFromRoute("compartment"));
        builder.Services.AddSingleton<IAuthorizationHandler, RefuseMallory>();
        builder.Services.AddAuthentication(HeaderIdentity.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderIdentity>(HeaderIdentity.SchemeName, null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("member", p => p.RequireAuthenticatedUser());
            o.AddPolicy("admin", p => p.RequireClaim("role", "admin"));
        });

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGroup("/api/t/{compartment}").MapTrashEndpoints().RequireAuthorization("member");
        _app.MapGroup("/api/shared").MapTrashEndpoints().InSharedSpace().RequireAuthorization("member");
        _app.MapGroup("/api/admin").MapTrashAdminEndpoints().RequireAuthorization("admin");
        await _app.StartAsync();
    }

    /// <summary>The routes the host serves, conventions applied.</summary>
    public IReadOnlyList<RouteEndpoint> Endpoints
        => ((IEndpointRouteBuilder)(_app ?? throw new InvalidOperationException("The host is not started."))).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();

    /// <summary>An HTTP client to the host, as <paramref name="subject"/> (anonymous when null).</summary>
    public HttpClient Http(string? subject, string? role = null)
    {
        var http = Server.CreateClient();
        if (subject is not null)
            http.DefaultRequestHeaders.Add(HeaderIdentity.SubjectHeader, subject);
        if (role is not null)
            http.DefaultRequestHeaders.Add(HeaderIdentity.RoleHeader, role);
        return http;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private sealed class RefuseMallory : AuthorizationHandler<OperationRequirement, TrashItem>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OperationRequirement requirement, TrashItem resource)
        {
            if (context.User.FindFirst("sub")?.Value != "mallory")
                context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class HeaderIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "header";
        public const string SubjectHeader = "X-Test-Subject";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(SubjectHeader, out var subject))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new("sub", subject.ToString()) };
            if (Request.Headers.TryGetValue(RoleHeader, out var role))
                claims.Add(new Claim("role", role.ToString()));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}

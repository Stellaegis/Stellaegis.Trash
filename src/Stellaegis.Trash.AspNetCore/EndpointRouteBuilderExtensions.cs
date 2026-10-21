using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;

namespace Stellaegis.Trash.AspNetCore;

/// <summary>
/// Maps the trash API: <see cref="ITrashService"/> exposed over HTTP, in the space the host resolves, each route
/// labelled with its operation from <see cref="TrashOperations"/> and none secured — the host attaches its
/// security to the groups returned.
/// </summary>
/// <remarks>
/// <para>Endpoints of <see cref="MapTrashEndpoints"/>, relative to the builder they are mapped on:</para>
/// <list type="bullet">
///   <item><c>POST trash</c> (<c>put</c>) — a <see cref="TrashRequestContract"/> as JSON, the payload in base64;
///   answers <c>201</c> with the item and its location</item>
///   <item><c>GET trash?sourceKind=…</c> (<c>list</c>) — the items of the space still in the trash</item>
///   <item><c>GET trash/{id}</c> (<c>read</c>) — what identifies an item</item>
///   <item><c>GET trash/{id}/payload</c> (<c>read_payload</c>) — the item with its payload, not restored</item>
///   <item><c>POST trash/{id}/restore</c> (<c>restore</c>) — the item with its payload, recorded as restored</item>
///   <item><c>DELETE trash/{id}</c> (<c>delete</c>) — deleted for good</item>
/// </list>
/// <para>
/// The host answers <c>AuthorizeResourceAsync</c>, asked on each item a route loads, with an
/// <c>AuthorizationHandler&lt;OperationRequirement, TrashItem&gt;</c>, or declares <c>AllowAnyResource()</c>; a refusal
/// answers <c>404</c>, like an absence. The module accepts the shared space: the host reaches it by mapping the
/// endpoints a second time on a group marked <c>InSharedSpace()</c>. Errors are answered as problem details with
/// their code, <see cref="TrashErrors"/>. The routes carry no name, so the module maps several times in one host.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// builder.Services.AddStellaegisFrame(ApplicationFrame.ManyCompartments().WithSharedSpace());
/// builder.Services.AddStellaegisRequestContext(c => c.CompartmentFromRoute("org"));
/// builder.Services.AddStellaegisTrashPostgreSQL(builder.Configuration);
///
/// app.MapGroup("/api/orgs/{org}").MapTrashEndpoints()
///    .RequireAuthorizationPerOperation(op => op.ToString()).AllowAnyResource();
/// app.MapGroup("/api/admin").MapTrashAdminEndpoints().RequireAuthorization("platform-admin");
/// </code>
/// </example>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>Maps the trash endpoints on a group named <c>trash</c>, none secured.</summary>
    /// <returns>The group holding the endpoints, for the host's security and further conventions.</returns>
    public static RouteGroupBuilder MapTrashEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Guard.NotNull(endpoints);

        var group = endpoints.MapGroup("trash")
            .BelongsTo<TrashModule>()
            .RequireRequestContext();
        group.AddErrorTranslation();

        group.MapPost("/", async (HttpContext http, TrashRequestContract request, IRequestContext context, ITrashService trash, CancellationToken ct) =>
            {
                if (context.Space is not { } space)
                    return Results.NotFound();
                if (!TryDecode(request.Payload, out var payload))
                    return Results.Problem(detail: "The payload is not valid base64.", statusCode: StatusCodes.Status400BadRequest, extensions: Code(TrashErrors.InvalidRequest));

                var item = await trash.TrashAsync(space, new TrashRequest
                {
                    SourceKind = request.SourceKind ?? string.Empty,
                    SourceUniqueId = request.SourceUniqueId,
                    DisplayName = request.DisplayName ?? string.Empty,
                    ContentType = request.ContentType ?? "application/json",
                    Payload = payload,
                    PurgeAt = request.PurgeAt,
                }, context.Actor, ct);
                return Results.Created(Location(http, item.TrashItemUniqueId), TrashItemResponse.From(item));
            })
            .WithMetadata(TrashOperations.Put)
            .Produces<TrashItemResponse>(StatusCodes.Status201Created);

        group.MapGet("/", async (HttpContext http, string? sourceKind, IRequestContext context, ITrashService trash, CancellationToken ct) =>
            {
                if (context.Space is not { } space)
                    return Results.NotFound();

                var allowed = new List<TrashItemResponse>();
                foreach (var item in await trash.ListAsync(space, context.Actor, sourceKind, ct))
                {
                    if (await http.AuthorizeResourceAsync(item))
                        allowed.Add(TrashItemResponse.From(item));
                }
                return Results.Ok(allowed);
            })
            .WithMetadata(TrashOperations.List)
            .Produces<IReadOnlyList<TrashItemResponse>>();

        group.MapGet("/{id:guid}", async (HttpContext http, Guid id, IRequestContext context, ITrashService trash, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, context, trash, id, ct);
                return access.Failure ?? Results.Ok(TrashItemResponse.From(access.Item!));
            })
            .WithMetadata(TrashOperations.Read)
            .Produces<TrashItemResponse>();

        group.MapGet("/{id:guid}/payload", async (HttpContext http, Guid id, IRequestContext context, ITrashService trash, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, context, trash, id, ct);
                if (access.Failure is not null)
                    return access.Failure;
                var opened = await trash.OpenAsync(access.Space, id, access.Actor, ct);
                return opened is null ? Results.NotFound() : Results.Ok(TrashedObjectResponse.From(opened));
            })
            .WithMetadata(TrashOperations.ReadPayload)
            .Produces<TrashedObjectResponse>();

        group.MapPost("/{id:guid}/restore", async (HttpContext http, Guid id, IRequestContext context, ITrashService trash, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, context, trash, id, ct);
                if (access.Failure is not null)
                    return access.Failure;
                return Results.Ok(TrashedObjectResponse.From(await trash.RestoreAsync(access.Space, id, access.Actor, ct)));
            })
            .WithMetadata(TrashOperations.Restore)
            .Produces<TrashedObjectResponse>();

        group.MapDelete("/{id:guid}", async (HttpContext http, Guid id, IRequestContext context, ITrashService trash, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, context, trash, id, ct);
                if (access.Failure is not null)
                    return access.Failure;
                await trash.DeleteAsync(access.Space, id, access.Actor, ct);
                return Results.NoContent();
            })
            .WithMetadata(TrashOperations.Delete)
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }

    /// <summary>
    /// Maps the administration of the trash on a group named <c>trash</c>, acting on compartments — the
    /// compartment is the object of these routes, a segment of their path, and the request context gives the
    /// actor —, none secured. A host that does not map it has no purge.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>DELETE trash/compartments/{compartment}</c> (<c>purge</c>) — every item of the compartment deleted
    ///   for good, answering how many; a replayed purge completes an interrupted one. Not offered under an isolation
    ///   by owner</item>
    ///   <item><c>DELETE trash/compartments/{compartment}/expired</c> (<c>purge_expired</c>) — the items whose
    ///   deadline has passed deleted, answering how many</item>
    /// </list>
    /// <c>404</c> for a value that is not a compartment: the shared space has no purge.
    /// </remarks>
    /// <returns>The group holding the endpoints, for the host's security and further conventions.</returns>
    public static RouteGroupBuilder MapTrashAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Guard.NotNull(endpoints);

        var group = endpoints.MapGroup("trash")
            .BelongsTo<TrashModule>()
            .RequireRequestContext(SharedSpaceAcceptance.Refused)
            .ActsOnSpaces();
        group.AddErrorTranslation();

        group.MapDelete("/compartments/{compartment}", async (string compartment, IRequestContext context, ITrashService trash, CancellationToken ct) =>
            {
                if (!Compartment.TryParse(compartment, out var target))
                    return Results.NotFound();
                var purged = await trash.PurgeCompartmentAsync(target, context.Actor, ct);
                return Results.Ok(new PurgeResponse(purged));
            })
            .WithMetadata(TrashOperations.Purge)
            .LoadsNoResource()
            .Produces<PurgeResponse>();

        group.MapDelete("/compartments/{compartment}/expired", async (string compartment, IRequestContext context, ITrashService trash, CancellationToken ct) =>
            {
                if (!Compartment.TryParse(compartment, out var target))
                    return Results.NotFound();
                var purged = await trash.PurgeExpiredAsync(target, context.Actor, cancellationToken: ct);
                return Results.Ok(new PurgeResponse(purged));
            })
            .WithMetadata(TrashOperations.PurgeExpired)
            .LoadsNoResource()
            .Produces<PurgeResponse>();

        return group;
    }

    /// <summary>How many items a purge deleted.</summary>
    public sealed record PurgeResponse(int Purged);

    private static async Task<Access> AuthorizeAsync(HttpContext http, IRequestContext context, ITrashService trash, Guid id, CancellationToken ct)
    {
        if (context.Space is not { } space)
            return Access.Refused(Results.NotFound());

        // A refusal is indistinguishable from an absence, so a caller cannot probe an item's existence.
        var item = await trash.GetAsync(space, id, context.Actor, ct);
        if (item is null || !await http.AuthorizeResourceAsync(item))
            return Access.Refused(Results.NotFound());

        return new Access(space, context.Actor, item, null);
    }

    private static string Location(HttpContext http, Guid id)
        => $"{http.Request.PathBase}{http.Request.Path.Value?.TrimEnd('/')}/{id:N}";

    private static bool TryDecode(string? payload, out ReadOnlyMemory<byte> bytes)
    {
        if (payload is not null)
        {
            var buffer = new byte[payload.Length];
            if (Convert.TryFromBase64String(payload, buffer, out var written))
            {
                bytes = buffer.AsMemory(0, written);
                return true;
            }
        }
        bytes = default;
        return false;
    }

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };

    private sealed record Access(Space Space, Actor Actor, TrashItem? Item, IResult? Failure)
    {
        public static Access Refused(IResult failure) => new(null!, null!, null, failure);
    }
}

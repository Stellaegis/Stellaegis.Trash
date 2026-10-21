using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;
using Stellaegis.Trash.AspNetCore;
using static Stellaegis.Trash.Tests.Fixtures;

namespace Stellaegis.Trash.Tests;

/// <summary>The mapped endpoints as raw HTTP: what a browser or any other client meets.</summary>
public sealed class TrashApiTests(TrashHost host) : IClassFixture<TrashHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TrashRequestContract Contract(string json = """{"number":"F-1"}""", Guid? source = null, string sourceKind = "invoices", DateTimeOffset? purgeAt = null)
        => new(sourceKind, source ?? Guid.NewGuid(), "Invoice F-1", Convert.ToBase64String(Json(json).Span), PurgeAt: purgeAt);

    private static async Task<(Compartment Compartment, TrashItemResponse Item)> PutAsync(HttpClient http, string json = """{"number":"F-1"}""")
    {
        var compartment = NewCompartment();
        var created = await http.PostAsJsonAsync($"/api/t/{compartment}/trash", Contract(json), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (compartment, (await created.Content.ReadFromJsonAsync<TrashItemResponse>(Ct))!);
    }

    [Fact]
    public async Task Putting_answers_201_with_the_location_and_the_item()
    {
        var http = host.Http("alice");
        var compartment = NewCompartment();

        var created = await http.PostAsJsonAsync($"/api/t/{compartment}/trash", Contract(), Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = (await created.Content.ReadFromJsonAsync<TrashItemResponse>(Ct))!;
        Assert.Equal(("invoices", "Invoice F-1", "application/json", TrashItemStatus.Trashed, "alice", compartment), (item.SourceKind, item.DisplayName, item.ContentType, item.Status, item.TrashedBy.Value, item.Compartment));
        Assert.Equal($"/api/t/{compartment}/trash/{item.TrashItemUniqueId:N}", created.Headers.Location?.ToString());
    }

    [Fact]
    public async Task The_payload_is_read_restored_once_and_the_item_deleted()
    {
        var http = host.Http("alice");
        var (compartment, item) = await PutAsync(http, """{"number":"F-7"}""");
        var url = $"/api/t/{compartment}/trash/{item.TrashItemUniqueId}";

        var opened = (await http.GetFromJsonAsync<TrashedObjectResponse>($"{url}/payload", Ct))!;
        var restored = await http.PostAsync($"{url}/restore", null, Ct);
        var again = await http.PostAsync($"{url}/restore", null, Ct);
        var deleted = await http.DeleteAsync(url, Ct);

        Assert.Equal("""{"number":"F-7"}""", Text(Convert.FromBase64String(opened.Payload)));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var body = (await restored.Content.ReadFromJsonAsync<TrashedObjectResponse>(Ct))!;
        Assert.Equal((TrashItemStatus.Restored, "alice"), (body.Item.Status, body.Item.RestoredBy?.Value));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(TrashErrors.AlreadyRestored, (await again.ReadErrorAsync(Ct))!.Code);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(url, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_bad_request_answers_400_with_its_code()
    {
        var http = host.Http("alice");
        var compartment = NewCompartment();

        var badKind = await http.PostAsJsonAsync($"/api/t/{compartment}/trash", Contract(sourceKind: "Invoices"), Ct);
        var badBase64 = await http.PostAsJsonAsync($"/api/t/{compartment}/trash", new TrashRequestContract("invoices", Guid.NewGuid(), "x", "not base64!"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        Assert.Equal(TrashErrors.InvalidRequest, (await badKind.ReadErrorAsync(Ct))!.Code);
        Assert.Equal(HttpStatusCode.BadRequest, badBase64.StatusCode);
        Assert.Equal(TrashErrors.InvalidRequest, (await badBase64.ReadErrorAsync(Ct))!.Code);
    }

    [Fact]
    public async Task A_refused_item_is_absent_and_a_list_is_filtered()
    {
        var alice = host.Http("alice");
        var (compartment, item) = await PutAsync(alice);

        var mallory = host.Http("mallory");
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.GetAsync($"/api/t/{compartment}/trash/{item.TrashItemUniqueId}", Ct)).StatusCode);
        Assert.Empty((await mallory.GetFromJsonAsync<List<TrashItemResponse>>($"/api/t/{compartment}/trash", Ct))!);
        Assert.Single((await alice.GetFromJsonAsync<List<TrashItemResponse>>($"/api/t/{compartment}/trash?sourceKind=invoices", Ct))!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Http(null).GetAsync($"/api/t/{compartment}/trash", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_shared_space_is_reached_by_its_own_group_only()
    {
        var http = host.Http("alice");

        var created = await http.PostAsJsonAsync("/api/shared/trash", Contract(sourceKind: "templates"), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = (await created.Content.ReadFromJsonAsync<TrashItemResponse>(Ct))!;

        Assert.True(item.IsShared);
        Assert.Null(item.Compartment);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/t/{NewCompartment()}/trash/{item.TrashItemUniqueId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/shared/trash/{item.TrashItemUniqueId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_administration_purges_a_compartment_for_its_role_only()
    {
        var alice = host.Http("alice");
        var (compartment, _) = await PutAsync(alice);
        await alice.PostAsJsonAsync($"/api/t/{compartment}/trash", Contract(purgeAt: DateTimeOffset.UtcNow.AddDays(-1)), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/admin/trash/compartments/{compartment}", Ct)).StatusCode);

        var admin = host.Http("root", "admin");
        var expired = await admin.DeleteAsync($"/api/admin/trash/compartments/{compartment}/expired", Ct);
        Assert.Equal(1, (await expired.Content.ReadFromJsonAsync<EndpointRouteBuilderExtensions.PurgeResponse>(Ct))!.Purged);
        var purged = await admin.DeleteAsync($"/api/admin/trash/compartments/{compartment}", Ct);
        Assert.Equal(1, (await purged.Content.ReadFromJsonAsync<EndpointRouteBuilderExtensions.PurgeResponse>(Ct))!.Purged);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/admin/trash/compartments/Not-A-Compartment", Ct)).StatusCode);
        Assert.Empty((await alice.GetFromJsonAsync<List<TrashItemResponse>>($"/api/t/{compartment}/trash", Ct))!);
    }

    [Fact]
    public void Every_route_carries_an_operation_of_the_module_and_no_name()
    {
        var routes = host.Endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.Contains("trash", StringComparison.Ordinal)).ToList();

        Assert.Equal(14, routes.Count);
        Assert.All(routes, route =>
        {
            Assert.Contains(route.GetOperation(), TrashOperations.All);
            Assert.Null(route.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>());
        });
        Assert.Equal(2, routes.Count(route => route.GetOperation() == TrashOperations.Purge || route.GetOperation() == TrashOperations.PurgeExpired));
    }

    [Fact]
    public async Task An_unresolved_compartment_answers_404()
    {
        var http = host.Http("alice");

        var created = await http.PostAsJsonAsync("/api/t/Not-A-Compartment/trash", Contract(), Ct);

        Assert.Equal(HttpStatusCode.NotFound, created.StatusCode);
        Assert.Equal(JsonValueKind.Undefined, default(JsonElement).ValueKind);
    }
}

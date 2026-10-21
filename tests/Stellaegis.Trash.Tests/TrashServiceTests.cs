using Stellaegis.Primitives;
using static Stellaegis.Trash.Tests.Fixtures;

namespace Stellaegis.Trash.Tests;

/// <summary>The rules of the trash, on the in-memory store.</summary>
public sealed class TrashServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_object_goes_in_with_its_identity_and_its_deadline()
    {
        await using var harness = TrashHarness.InMemory();
        var acme = NewCompartment();
        var request = Request();

        var item = await harness.Trash.TrashAsync(acme, request, Alice, Ct);

        Assert.Equal((acme, "invoices", request.SourceUniqueId, "Invoice F-2026-001", "application/json", TrashItemStatus.Trashed, Alice),
            (item.Space.Compartment, item.SourceKind, item.SourceUniqueId, item.DisplayName, item.ContentType, item.Status, item.TrashedBy));
        Assert.Equal(harness.Clock.GetUtcNow(), item.TrashedAt);
        Assert.Equal(harness.Clock.GetUtcNow().AddDays(30), item.PurgeAt);
        Assert.Equal(request.Payload.Length, item.PayloadLength);
    }

    [Fact]
    public async Task The_deadline_follows_the_request_or_stays_open()
    {
        await using var harness = TrashHarness.InMemory(retentionDays: null);
        var acme = NewCompartment();

        var forever = await harness.Trash.TrashAsync(acme, Request(), Alice, Ct);
        var dated = await harness.Trash.TrashAsync(acme, Request(purgeAt: harness.Clock.GetUtcNow().AddDays(7)), Alice, Ct);
        var kept = await harness.Trash.TrashAsync(acme, Request(purgeAt: DateTimeOffset.MaxValue), Alice, Ct);

        Assert.Null(forever.PurgeAt);
        Assert.Equal(harness.Clock.GetUtcNow().AddDays(7), dated.PurgeAt);
        Assert.Null(kept.PurgeAt);
    }

    [Fact]
    public async Task A_restoration_hands_the_payload_back_once_and_records_who()
    {
        await using var harness = TrashHarness.InMemory();
        var acme = NewCompartment();
        var item = await harness.Trash.TrashAsync(acme, Request("""{"number":"F-1"}"""), Alice, Ct);
        harness.Clock.Advance(TimeSpan.FromHours(1));

        var restored = await harness.Trash.RestoreAsync(acme, item.TrashItemUniqueId, Bob, Ct);

        Assert.Equal("""{"number":"F-1"}""", Text(restored.Payload));
        Assert.Equal((TrashItemStatus.Restored, Bob, harness.Clock.GetUtcNow(), item.SourceUniqueId),
            (restored.Item.Status, restored.Item.RestoredBy, restored.Item.RestoredAt, restored.Item.SourceUniqueId));
        var again = await Assert.ThrowsAsync<TrashItemRestoredException>(() => harness.Trash.RestoreAsync(acme, item.TrashItemUniqueId, Bob, Ct));
        Assert.Equal(TrashErrors.AlreadyRestored, again.Error.Code);
        Assert.Equal(TrashItemStatus.Restored, (await harness.Trash.GetAsync(acme, item.TrashItemUniqueId, Alice, Ct))!.Status);
        Assert.Empty(await harness.Trash.ListAsync(acme, Alice, cancellationToken: Ct));
    }

    [Fact]
    public async Task Trashing_the_same_object_again_replaces_its_item_and_keeps_its_identifier()
    {
        await using var harness = TrashHarness.InMemory();
        var acme = NewCompartment();
        var source = Guid.NewGuid();
        var first = await harness.Trash.TrashAsync(acme, Request("""{"v":1}""", source), Alice, Ct);
        await harness.Trash.RestoreAsync(acme, first.TrashItemUniqueId, Alice, Ct);

        var second = await harness.Trash.TrashAsync(acme, Request("""{"v":2}""", source, displayName: "Invoice F-1 (v2)"), Bob, Ct);

        Assert.Equal(first.TrashItemUniqueId, second.TrashItemUniqueId);
        Assert.Equal((TrashItemStatus.Trashed, Bob, "Invoice F-1 (v2)"), (second.Status, second.TrashedBy, second.DisplayName));
        Assert.Equal("""{"v":2}""", Text((await harness.Trash.OpenAsync(acme, second.TrashItemUniqueId, Alice, Ct))!.Payload));
        Assert.Single(await harness.Trash.ListAsync(acme, Alice, cancellationToken: Ct));
    }

    [Fact]
    public async Task An_expired_item_is_not_restored_and_the_purge_of_the_expired_deletes_it()
    {
        await using var harness = TrashHarness.InMemory(retentionDays: 1);
        var acme = NewCompartment();
        var expiring = await harness.Trash.TrashAsync(acme, Request(), Alice, Ct);
        var kept = await harness.Trash.TrashAsync(acme, Request(purgeAt: DateTimeOffset.MaxValue), Alice, Ct);
        harness.Clock.Advance(TimeSpan.FromDays(2));

        var expired = await Assert.ThrowsAsync<TrashItemExpiredException>(() => harness.Trash.RestoreAsync(acme, expiring.TrashItemUniqueId, Alice, Ct));
        Assert.Equal(TrashErrors.Expired, expired.Error.Code);

        Assert.Equal(1, await harness.Trash.PurgeExpiredAsync(acme, Purger, cancellationToken: Ct));
        Assert.Equal(0, await harness.Trash.PurgeExpiredAsync(acme, Purger, cancellationToken: Ct));
        Assert.Null(await harness.Trash.GetAsync(acme, expiring.TrashItemUniqueId, Alice, Ct));
        Assert.NotNull(await harness.Trash.GetAsync(acme, kept.TrashItemUniqueId, Alice, Ct));
    }

    [Fact]
    public async Task A_deletion_is_final_and_an_absent_item_is_not_found()
    {
        await using var harness = TrashHarness.InMemory();
        var acme = NewCompartment();
        var item = await harness.Trash.TrashAsync(acme, Request(), Alice, Ct);

        await harness.Trash.DeleteAsync(acme, item.TrashItemUniqueId, Alice, Ct);

        Assert.Null(await harness.Trash.GetAsync(acme, item.TrashItemUniqueId, Alice, Ct));
        var missing = await Assert.ThrowsAsync<TrashItemNotFoundException>(() => harness.Trash.DeleteAsync(acme, item.TrashItemUniqueId, Alice, Ct));
        Assert.Equal(TrashErrors.NotFound, missing.Error.Code);
        await Assert.ThrowsAsync<TrashItemNotFoundException>(() => harness.Trash.RestoreAsync(acme, Guid.NewGuid(), Alice, Ct));
    }

    [Fact]
    public async Task A_space_never_sees_the_items_of_another()
    {
        await using var harness = TrashHarness.InMemory();
        var acme = NewCompartment();
        var globex = NewCompartment();
        var ofAcme = await harness.Trash.TrashAsync(acme, Request(), Alice, Ct);
        var shared = await harness.Trash.TrashAsync(Space.Shared, Request(sourceKind: "templates", displayName: "Template"), Alice, Ct);

        Assert.Null(await harness.Trash.GetAsync(globex, ofAcme.TrashItemUniqueId, Alice, Ct));
        Assert.Null(await harness.Trash.GetAsync(Space.Shared, ofAcme.TrashItemUniqueId, Alice, Ct));
        Assert.Null(await harness.Trash.GetAsync(acme, shared.TrashItemUniqueId, Alice, Ct));
        Assert.Equal([ofAcme.TrashItemUniqueId], (await harness.Trash.ListAsync(acme, Alice, cancellationToken: Ct)).Select(i => i.TrashItemUniqueId));
        Assert.Equal([shared.TrashItemUniqueId], (await harness.Trash.ListAsync(Space.Shared, Alice, cancellationToken: Ct)).Select(i => i.TrashItemUniqueId));
        Assert.Empty(await harness.Trash.ListAsync(globex, Alice, cancellationToken: Ct));
    }

    [Fact]
    public async Task The_list_filters_by_kind_and_stops_at_the_limit()
    {
        await using var harness = TrashHarness.InMemory(listLimit: 2);
        var acme = NewCompartment();
        await harness.Trash.TrashAsync(acme, Request(sourceKind: "invoices"), Alice, Ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Trash.TrashAsync(acme, Request(sourceKind: "parties", displayName: "Globex"), Alice, Ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var latest = await harness.Trash.TrashAsync(acme, Request(sourceKind: "invoices"), Alice, Ct);

        var all = await harness.Trash.ListAsync(acme, Alice, cancellationToken: Ct);
        var invoices = await harness.Trash.ListAsync(acme, Alice, "invoices", Ct);

        Assert.Equal(2, all.Count);
        Assert.Equal(latest.TrashItemUniqueId, all[0].TrashItemUniqueId);
        Assert.All(invoices, item => Assert.Equal("invoices", item.SourceKind));
        await Assert.ThrowsAsync<TrashRequestException>(() => harness.Trash.ListAsync(acme, Alice, "Invoices", Ct));
    }

    [Fact]
    public async Task The_purge_of_a_compartment_deletes_everything_in_batches_and_replays()
    {
        await using var harness = TrashHarness.InMemory();
        var acme = NewCompartment();
        var globex = NewCompartment();
        for (var i = 0; i < 230; i++)
            await harness.Trash.TrashAsync(acme, Request(), Alice, Ct);
        var restored = await harness.Trash.TrashAsync(acme, Request(), Alice, Ct);
        await harness.Trash.RestoreAsync(acme, restored.TrashItemUniqueId, Alice, Ct);
        var other = await harness.Trash.TrashAsync(globex, Request(), Alice, Ct);

        Assert.Equal(231, await harness.Trash.PurgeCompartmentAsync(acme, Purger, Ct));
        Assert.Equal(0, await harness.Trash.PurgeCompartmentAsync(acme, Purger, Ct));
        Assert.NotNull(await harness.Trash.GetAsync(globex, other.TrashItemUniqueId, Alice, Ct));
    }

    [Theory]
    [InlineData("Invoices", "", TrashErrors.InvalidRequest)]
    [InlineData("invoices", " ", TrashErrors.InvalidRequest)]
    [InlineData("invoice-lines", "x", TrashErrors.InvalidRequest)]
    public async Task A_request_that_describes_no_object_is_refused_with_a_code(string sourceKind, string displayName, string code)
    {
        await using var harness = TrashHarness.InMemory();
        var request = new TrashRequest { SourceKind = sourceKind, SourceUniqueId = Guid.NewGuid(), DisplayName = displayName, Payload = Json("{}") };

        var exception = await Assert.ThrowsAsync<TrashRequestException>(() => harness.Trash.TrashAsync(NewCompartment(), request, Alice, Ct));

        Assert.Equal(code, exception.Error.Code);
    }

    [Fact]
    public async Task An_empty_identifier_an_empty_payload_and_a_payload_too_large_are_refused()
    {
        await using var harness = TrashHarness.InMemory(maxPayloadLength: 8);
        var acme = NewCompartment();

        var emptyId = await Assert.ThrowsAsync<TrashRequestException>(() => harness.Trash.TrashAsync(acme, Request(sourceUniqueId: Guid.Empty), Alice, Ct));
        var emptyPayload = await Assert.ThrowsAsync<TrashRequestException>(() => harness.Trash.TrashAsync(acme, Request(""), Alice, Ct));
        var tooLarge = await Assert.ThrowsAsync<TrashRequestException>(() => harness.Trash.TrashAsync(acme, Request("""{"n":"123456"}"""), Alice, Ct));

        Assert.Equal(TrashErrors.InvalidRequest, emptyId.Error.Code);
        Assert.Equal(TrashErrors.InvalidRequest, emptyPayload.Error.Code);
        Assert.Equal(TrashErrors.PayloadTooLarge, tooLarge.Error.Code);
    }
}

# Stellaegis.Trash

Stellaegis.Trash is a .NET library that gives an application a trash: an object the user deletes is
serialized by the application and kept aside, with its identity, until somebody restores it or its
deadline passes. A restored object is recreated by the application under the identifier it had, so
nothing that pointed to it breaks. Your code depends on one interface, `ITrashService`; the adapter
you register decides where the items live — in memory for development, or in PostgreSQL, encrypted.

The trash interprets nothing of what it keeps: a payload is bytes, with a media type. It is the
application's trash, for its own objects — an invoice, a contact, a template —, not a trash of
files; a document store keeps its own.

The library targets .NET 10 and is released under the MIT license.

## Packages

| Package | Contents | Depends on |
| --- | --- | --- |
| `Stellaegis.Trash` | `ITrashService`, `TrashItem`, `TrashRequest`, the module and its operations, the options, the API contracts, and the in-memory store | `Stellaegis.Primitives`, `Stellaegis.Primitives.Hosting` |
| `Stellaegis.Trash.PostgreSQL` | The PostgreSQL store: the schema `trash`, its script, payloads encrypted with pgcrypto, row-level security | `Stellaegis.Primitives.Data`, Npgsql, Dapper |
| `Stellaegis.Trash.AspNetCore` | Endpoints that expose the trash over HTTP, and the administration of its purges | `Stellaegis.Primitives.AspNetCore`, ASP.NET Core |

## Trashing an object

Register the trash with a store, and inject `ITrashService`:

```csharp
builder.Services.AddStellaegisFrame(ApplicationFrame.ManyCompartments().WithSharedSpace());
builder.Services.AddStellaegisTrashPostgreSQL(builder.Configuration);   // or AddStellaegisTrashInMemory
```

```csharp
public sealed class InvoiceDeletion(ITrashService trash, IInvoiceRepository invoices)
{
    public async Task DeleteAsync(Space space, Invoice invoice, Actor actor, CancellationToken ct)
    {
        await trash.TrashAsync(space, new TrashRequest
        {
            SourceKind = "invoices",
            SourceUniqueId = invoice.InvoiceUniqueId,
            DisplayName = invoice.Number,
            Payload = JsonSerializer.SerializeToUtf8Bytes(invoice),
        }, actor, ct);
        await invoices.DeleteAsync(invoice.InvoiceUniqueId, ct);
    }

    public async Task RestoreAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken ct)
    {
        var restored = await trash.RestoreAsync(space, trashItemUniqueId, actor, ct);
        var invoice = JsonSerializer.Deserialize<Invoice>(restored.Payload.Span)!;
        await invoices.InsertAsync(invoice with { InvoiceUniqueId = restored.Item.SourceUniqueId }, ct);
    }
}
```

Trash first, then delete. A crash in between leaves a trashed copy of an object that still exists,
which the next deletion replaces, rather than an object that is nowhere: trashing is idempotent —
the same kind and identifier in the same space replace their previous item, restored or not.

Every call acts in a `Space` — a compartment, or the shared space — for an `Actor`, which the item
records: who trashed it, and who restored it. Recreating the object from the payload is the
application's work, as is giving it back its identity, `TrashItem.SourceUniqueId`: the trash never
hands that identifier to anything else.

### What an item is

`TrashItem` is what identifies an item without its payload: the identifier the routes carry, the
space, the kind and identifier of the object, what to show for it, the media type and length of the
payload, its status — `Trashed` or `Restored` —, when and by whom it was trashed, its deadline, and
when and by whom it was restored. `TrashedObject` adds the payload, as the application gave it.

| Call | What it does |
| --- | --- |
| `TrashAsync` | Puts an object in; answers the item. `SourceKind` is an identifier of the family (`invoices`, `purchase_orders`), the display name 1 to 256 characters, the payload at least one byte and at most `MaxPayloadLength`. |
| `ListAsync` | The items of the space still in the trash, the most recently trashed first, at most `ListLimit` of them; of one kind when asked. |
| `GetAsync`, `OpenAsync` | What identifies an item, or the item with its payload, without restoring it; `null` when the space has none. |
| `RestoreAsync` | Hands the payload back, once, before the deadline, and records the item as restored. The item stays, as the trace of both acts, until its deadline. |
| `DeleteAsync` | Deletes an item for good, before its deadline. |
| `PurgeExpiredAsync` | Deletes the items of the space whose deadline has passed, restored or not; answers how many. |
| `PurgeCompartmentAsync` | Deletes every item of a compartment, whatever its state, in batches: a replayed purge completes an interrupted one. |

An item's deadline, `PurgeAt`, is set when it is trashed: what the request says, or now plus the
module's `DefaultRetention`, thirty days unless configured. A request asking `DateTimeOffset.MaxValue`,
or a module whose retention is unset, keeps the item until somebody deletes it. The deadline is a
date the purge reads; nothing runs by itself. A scheduled task of the application calls
`PurgeExpiredAsync` on each space, under a name of its own — `Actor.Job("trash_purge")` —, or an
administrator calls the endpoint below.

### Options

```json
{
  "Stellaegis": {
    "Trash": {
      "DefaultRetention": "30.00:00:00",
      "ListLimit": 500,
      "MaxPayloadLength": 16777216
    }
  }
}
```

The options are read from the section `Stellaegis:Trash`, validated when the host starts; pass
`sectionName` to read another one, or configure them in code with the overload taking an
`Action<TrashOptions>`.

## Spaces, actors and the module

The trash is a module of the Stellaegis family, `TrashModule`, declared to the application frame by
its registration: it has compartments, accepts the shared space, and holds eight operations,
`TrashOperations` — `trash_items.put`, `list`, `read`, `read_payload`, `restore`, `delete`,
`purge_expired` and `purge`. They are what a host grants.

- **A compartment** is the usual space: a tenant, an organization. Items of a compartment are seen
  by nobody outside it, and the purge of a compartment removes them all when the compartment goes.
- **The shared space** holds the objects that belong to no compartment — templates, reference
  data —, in an application whose frame declares `WithSharedSpace()`. It has no purge: nothing of it
  goes away with a compartment.
- **The actor** is recorded on every act, and never inferred: it is a parameter of each call, the
  one the request context resolved for a route.
- **An isolation by owner**, declared by the frame as `WithOwnerIsolation()`, keeps each actor to
  the items it trashed. The PostgreSQL store declares the capability and the installation applies its
  rule; the in-memory store does not, and a frame declaring it fails at startup against it. The purge
  of a compartment is not meant for this isolation: it would delete the items of its caller only.

## The PostgreSQL store

The adapter declares the trash's database to Primitives, as `TrashDatabase.Declaration`: the schema
`trash`, its script embedded in the package, the pgcrypto extension, the table under forced row-level
security, and the rule of the isolation by owner, applied when the frame declares it. The database is
**installed** by `DatabaseLifecycle.InstallAsync` of `Stellaegis.Primitives.Data`, from an installer
or the provisioner of your application, under a role that owns the schema — it creates the schema
and the extension, applies the script, records it in `trash._schema_migrations`, and grants the
application's role its data rights and no DDL. The application connects under that ordinary role,
and **verifies** the database at every startup with the other databases it declares:

```csharp
// The installer, under a role that may create the schema and the extension.
await DatabaseLifecycle.InstallAsync([TrashDatabase.Declaration], frame, OpenAsAdminAsync, "myapp_app");

// The application, at startup, under its own role.
builder.Services.AddStellaegisTrashPostgreSQL(builder.Configuration);
builder.Services.AddStellaegisDatabaseVerification();   // a generic host; an ASP.NET Core host calls
                                                         // app.Services.VerifyStellaegisDatabasesAsync() before running
```

Rerunning the installation applies what is missing and records nothing twice. The verification fails
the startup when the schema is absent, when a script was applied with another content, when the
database holds a version this package does not know, or when the isolation by owner does not match
the frame.

```json
{
  "Stellaegis": {
    "Trash": {
      "PostgreSQL": {
        "ConnectionString": "Host=localhost;Database=myapp;Username=myapp_app;Password=...",
        "Passphrases": {
          "ActiveKeySlot": 1,
          "Keys": { "1": "set from an environment variable, never committed" }
        }
      }
    }
  }
}
```

The options are read from the section `Stellaegis:Trash:PostgreSQL`, and the trash's own from
`Stellaegis:Trash`; both are validated when the host starts.

### Encryption of the payloads

A payload is encrypted by pgcrypto, `pgp_sym_encrypt_bytea`, under a passphrase that comes from the
configuration of the process — a secret store or the environment, never a committed file, never
another table of the database. A reader of the database without it reads nothing of what was
trashed; what identifies an item stays readable, so that a user can see what the trash holds.

Two slots let the passphrase rotate without downtime: new writes use the active slot, each row
records the slot that encrypted it, and reading uses the slot of the row. To rotate, set the other
slot's passphrase and make it active; the retired passphrase stays configured as long as a row uses
it — until the last item encrypted under it is restored, deleted or purged, thirty days at most under
the default retention.

### Row-level security

Every query of the store filters on the space, and the database repeats the filter: the store opens
each connection in a space, set in the session context as `Stellaegis.Primitives.Data` does, and the
policy `pl_items_space` makes a row of another space invisible, whatever the query. The security is
forced, so that the owner of the table is bound by it too. Under an isolation by owner, the
restrictive policy `pl_items_owner` further keeps each actor to the items it trashed.

## The endpoints

`Stellaegis.Trash.AspNetCore` exposes `ITrashService` over HTTP, on a group named `trash` mapped
where the host decides, in the space the host's request context resolves. The routes carry no
security: the host attaches its own to the group returned.

```csharp
builder.Services.AddStellaegisFrame(ApplicationFrame.ManyCompartments().WithSharedSpace());
builder.Services.AddStellaegisRequestContext(c => c.CompartmentFromRoute("org"));
builder.Services.AddStellaegisTrashPostgreSQL(builder.Configuration);

app.MapGroup("/api/orgs/{org}").MapTrashEndpoints()
   .RequireAuthorizationPerOperation(op => op.ToString()).AllowAnyResource();
app.MapGroup("/api/shared").MapTrashEndpoints().InSharedSpace()
   .RequireAuthorization("editor").AllowAnyResource();
app.MapGroup("/api/admin").MapTrashAdminEndpoints().RequireAuthorization("platform-admin");
```

| Route | Operation | Answers |
| --- | --- | --- |
| `POST trash` | `put` | `201` with the item and its location. The body is a `TrashRequestContract`: the payload in base64, with its kind, identifier, display name, media type and deadline. |
| `GET trash?sourceKind=…` | `list` | `200` with the items of the space still in the trash. |
| `GET trash/{id}` | `read` | `200` with what identifies the item. |
| `GET trash/{id}/payload` | `read_payload` | `200` with the item and its payload in base64, not restored. |
| `POST trash/{id}/restore` | `restore` | `200` with the item and its payload, recorded as restored. |
| `DELETE trash/{id}` | `delete` | `204`. |
| `DELETE trash/compartments/{compartment}` | `purge` | `200` with how many items the purge deleted; administration. |
| `DELETE trash/compartments/{compartment}/expired` | `purge_expired` | `200` with how many expired items the purge deleted; administration. |

Each route carries its operation, so a host authorizes them one by one; none carries a name, so a
host maps the group several times. The routes of an item ask the host to authorize each item they
load, through `AuthorizeResourceAsync`: the host answers with an
`AuthorizationHandler<OperationRequirement, TrashItem>`, or declares `AllowAnyResource()` on the
group. A refusal answers `404`, like an absence, and a list keeps the items the host allows: a caller
cannot probe what it may not see. The shared space is reached by mapping the endpoints a second time,
on a group marked `InSharedSpace()`.

The administration acts on compartments: the compartment is a segment of the path, the request
context gives the actor, and the two purges load no resource. A value that is not a compartment
answers `404`; the shared space has no purge. A host that does not map the administration has no
purge over HTTP.

## Errors

| Exception | Code | Over HTTP |
| --- | --- | --- |
| `TrashRequestException` | `TrashRequest.Invalid`, `TrashRequest.PayloadTooLarge` | `400` |
| `TrashItemNotFoundException` | `TrashItem.NotFound` | `404` |
| `TrashItemRestoredException` | `TrashItem.AlreadyRestored` | `409` |
| `TrashItemExpiredException` | `TrashItem.Expired` | `409` |

Each is a `UserFacingException` of `Stellaegis.Primitives`, carrying its code and a message meant for
the caller; over HTTP, the endpoints answer problem details carrying the code, `TrashErrors`.

## Building and testing

```sh
dotnet test --solution Stellaegis.Trash.slnx
```

The PostgreSQL tests need a server. By default they connect to `127.0.0.1` as `postgres`, and create
the databases they use if they do not exist, `stellaegis_trash_tests` and its variants. Set
`STELLAEGIS_TRASH_TEST_POSTGRES` to another connection string to change that. The tests install the
schema as an application does, with `DatabaseLifecycle.InstallAsync` under that connection, then run
under an ordinary role they create, `stellaegis_trash_app`. When no server answers, these tests are
skipped and the others still run.

## License

MIT — see [LICENSE](https://github.com/Stellaegis/Stellaegis.Trash/blob/main/LICENSE). The packages
depend on third-party components listed in
[THIRD-PARTY-NOTICES.md](https://github.com/Stellaegis/Stellaegis.Trash/blob/main/THIRD-PARTY-NOTICES.md).

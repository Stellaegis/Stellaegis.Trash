# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [5.0.3] — 2026-11-16

On `Stellaegis.Primitives` 6.0.0, whose `Stellaegis.Primitives.Data` no longer references Dapper: an application
that takes the PostgreSQL adapter of the trash no longer receives Dapper through it. What the packages do is
unchanged.

### Changed

- **`Stellaegis.Primitives` 6.0.0 and Microsoft.Extensions 10.0.15.** Nothing changes in this module's code. The
  adapter stopped using Dapper in 5.0.2; until this version it still came with `Stellaegis.Primitives.Data` 5.x,
  which the adapter references.

### Updating from 5.0.2

Take the version: a host has nothing to change in what it asks of this module. The version brings Primitives 6.0.0
to the host, and two things follow from that, both described in the CHANGELOG of Primitives, § *Updating from 5.6.0*:

1. **The other packages of the family the host references take a version that runs without what
   `Stellaegis.Primitives.Data` 6.0.0 removed.** That section lists the least version of each. An older PostgreSQL
   or SQL Server adapter restores on Primitives 6 and fails at its first query.
2. **Code of the host that used Dapper without referencing it now references it itself**: Dapper came through
   the adapter, and no longer does.

## [5.0.2] — 2026-11-16

The store talks to PostgreSQL through Npgsql, and the adapter no longer references Dapper. What the packages do is
unchanged, and so is their SQL.

### Changed

- **`Stellaegis.Trash.PostgreSQL` no longer references Dapper.** Its repository runs `NpgsqlCommand`, each parameter
  under its declared type, each row read by the names of its columns. Dapper still comes with
  `Stellaegis.Primitives.Data` 5.x, which the adapter references.

### Updating from 5.0.1

Take the version: a host has nothing to change.

## [5.0.1] — 2026-11-09

On `Stellaegis.Primitives` 5.6.0 and Microsoft.Extensions 10.0.14, so that a host taking this module takes the
version of Primitives that closes what a badly assembled host let through. Nothing changes in the module's code.

### Changed

- **`Stellaegis.Primitives` 5.6.0 and Microsoft.Extensions 10.0.14.**

### Updating from 5.0.0

Take the version. A host meets what the startup of Primitives 5.6.0 now refuses, listed with its way out in the
CHANGELOG of Primitives, § *Updating from 5.5.0* — for the trash, mostly this: on PostgreSQL, the application connects
with its own role, never a superuser, a `BYPASSRLS` role nor the owner of the schema of the trash.

## [5.0.0] — 2026-10-21

The first version published on its own, as a module of the Stellaegis family on Stellaegis.Primitives 5.0.0. The
three packages move together, at the major version of the family. The framework's trash was a skeleton: three
projects, a store of items carrying a tenant slug, a host answering `/health`; nothing of it is kept as it was.

### Added

- `Stellaegis.Trash`: `ITrashService`, the trash an application puts its serialized objects in — put, idempotent
  on the kind and identifier of the object; list; read, with or without the payload; restore, once, before the
  deadline, the object keeping its identity; delete; the purge of the expired items; the purge of a compartment,
  in batches, replayable. `TrashItem`, what identifies an item: its space, the object it stands for, who trashed
  it and when, its deadline, who restored it and when. `TrashRequest`, what the application gives. `TrashModule`,
  declared with compartments, accepting the shared space, with its eight operations, `TrashOperations`.
  `TrashOptions`, bound from `Stellaegis:Trash` and validated at startup: the default retention, the limit of a
  list, the longest payload. The in-memory store, `AddStellaegisTrashInMemory`. The API contracts, in base64, for
  a client written against the endpoints. The errors, each a `UserFacingException` with its code:
  `TrashRequest.Invalid`, `TrashRequest.PayloadTooLarge`, `TrashItem.NotFound`, `TrashItem.AlreadyRestored`,
  `TrashItem.Expired`.
- `Stellaegis.Trash.PostgreSQL`: the store on the schema `trash`, declared to Primitives as
  `TrashDatabase.Declaration` — its script embedded, the pgcrypto extension, the table under forced row-level
  security, the rule of the isolation by owner applied when the frame declares it — and installed by
  `DatabaseLifecycle.InstallAsync`, verified at startup. Payloads encrypted with `pgp_sym_encrypt_bytea` under
  the passphrases of the process, `KeySlots`, two slots for a rotation. The store declares the capability
  `OwnerIsolation`. `AddStellaegisTrashPostgreSQL`, its options bound from `Stellaegis:Trash:PostgreSQL`.
- `Stellaegis.Trash.AspNetCore`: `MapTrashEndpoints`, the six routes of an item in the space the host resolves,
  each carrying its operation, each item authorized by the host, a refusal answered as an absence; the shared
  space by a second mapping. `MapTrashAdminEndpoints`, the two purges acting on compartments.

### Changed, from the framework's skeleton

- An item belongs to a `Space`, compartment or shared, instead of carrying a tenant slug; every call takes the
  actor it acts for.
- The typed unit of work, `ITrashUnitOfWork`, is gone: the store opens a unit of work of
  `Stellaegis.Primitives.Data` in the space, for the actor, and the database reads both in its policies.
- The host of the framework, with its own authentication, is gone: the endpoints map in the application's host,
  under its security.
- The passphrase comes from the configuration of the process, never from a vault the application calls.

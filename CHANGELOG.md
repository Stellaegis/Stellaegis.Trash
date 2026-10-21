# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

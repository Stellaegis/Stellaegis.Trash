# SQL scripts

The scripts of the `trash` schema are embedded in `Stellaegis.Trash.PostgreSQL`, under `Sql/PostgreSql/`, and applied by the
installation of the application through `DatabaseLifecycle.InstallAsync` of Stellaegis.Primitives.Data, which also
verifies them at every startup. Nothing here is passed by hand: see the README, *Installing the database*.

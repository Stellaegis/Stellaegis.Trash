# Third-party notices

Stellaegis.Trash is licensed under the MIT License (see [LICENSE](LICENSE)).

Its packages contain no third-party code. They reference the packages below, which NuGet installs
alongside them, each under its own license.

| Component | Used by | License (SPDX) | Project |
| --- | --- | --- | --- |
| Npgsql | `Stellaegis.Trash.PostgreSQL` | PostgreSQL | <https://github.com/npgsql/npgsql> |
| Dapper | `Stellaegis.Trash.PostgreSQL` | Apache-2.0 | <https://github.com/DapperLib/Dapper> |
| Stellaegis.Primitives, Stellaegis.Primitives.Hosting, Stellaegis.Primitives.Data, Stellaegis.Primitives.AspNetCore | all packages | MIT | <https://github.com/Stellaegis/Stellaegis.Primitives> |
| Microsoft.Extensions.\*, ASP.NET Core | all packages, through Stellaegis.Primitives.Hosting | MIT | <https://github.com/dotnet> |

The PostgreSQL store relies on the pgcrypto extension of the PostgreSQL server, under the PostgreSQL
License; it is installed on the server, not shipped by any package.

Test-only dependencies (xUnit, ASP.NET Core TestHost, Microsoft.Extensions.TimeProvider.Testing) are
not part of any package and are not listed here.

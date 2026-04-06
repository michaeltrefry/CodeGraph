# Project Structure Convention

All ~620 repositories follow a consistent solution structure:

```
TC.RepoNameApi/
├── TC.RepoNameApi.sln
├── TC.RepoNameApi/                # API host — controllers, Startup.cs, DI registration
├── TC.RepoNameApi.Models/         # Public contracts — published as NuGet package
├── TC.RepoNameApi.Services/       # Business logic
├── TC.RepoNameApi.Data/           # Data access (EF Core / Dapper)
└── TC.RepoNameJobs/               # Background jobs (external scheduler)
```

## Naming Convention

- All projects are prefixed with `TC.`
- The API project name matches the repository name: `TC.OrdersApi`
- Sub-projects append their role: `TC.OrdersApi.Models`, `TC.OrdersApi.Services`, etc.
- Jobs projects drop the "Api" and use "Jobs": `TC.OrdersJobs` (not `TC.OrdersApi.Jobs`)

## Key Roles

### TC.RepoNameApi (Host)
- ASP.NET WebApi host
- `Startup.cs` registers all DI, middleware, and configuration
- Controllers define REST endpoints
- References Services, Data, and Models

### TC.RepoNameApi.Models
- Published as a **NuGet package** (`TC.RepoNameApi.Models`)
- Contains public contracts: DTOs, request/response objects, events, enums
- Other repos reference this package to communicate with this service
- **This is the canonical linking key across repos** — the qualified type name (e.g., `TC.OrdersApi.Models.OrderCreatedEvent`) connects publishers to consumers

### TC.RepoNameApi.Services
- Business logic layer
- No direct database access — uses Data project interfaces
- Contains service classes registered via DI

### TC.RepoNameApi.Data
- Data access via EF Core (CRUD) and Dapper (complex queries)
- MySQL database
- Repository/store pattern

### TC.RepoNameJobs
- Background job classes
- Registered externally in a scheduler database with cron schedules
- Jobs project name drops "Api": `TC.OrdersJobs`

## Dependency Flow

```
Models ← Data ← Services ← Api (hosts everything)
                          ← Jobs
```

No references flow upward. Models has zero dependencies.

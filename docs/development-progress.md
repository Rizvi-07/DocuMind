# DocuMind development progress

Verified on **2026-10-08 (Europe/Berlin)** against the current working tree. Existing, uncommitted code and comment changes were included in inspection but are excluded from this step's commit. No AGENTS.md instructions were found in the repository or its ancestor directories.

## Actual status

DocuMind has a working database and Identity foundation, plus buildable API, worker, and frontend scaffolds. It does **not** yet implement account registration or document Q&A. A build proves compilation; it does not prove an authentication or RAG flow works.

### Complete and verified

- **Solution structure:** all four projects target net10.0 and build. API references Services and Data; Services references Data; Worker references Services and Data. These references compile, but referencing a project does not automatically register its services.
- **PostgreSQL and pgvector:** a read-only query through the existing local connection verified PostgreSQL **17.11** and vector extension **0.8.7**. The API readiness route returned **HTTP 200, Healthy**.
- **Identity schema:** the live database contains AspNetUsers, AspNetRoles, AspNetUserClaims, AspNetRoleClaims, AspNetUserLogins, AspNetUserRoles, and AspNetUserTokens. Migration history contains **20261007065231_InitialIdentity**. EF reports no pending model changes. No migration was applied or rolled back during inspection.
- **API scaffold:** controllers are mapped, development OpenAPI returns HTTP 200, and the sample weather endpoint returns five forecasts. Runtime OpenAPI lists only /WeatherForecast. The readiness route is mapped separately.
- **Worker scaffold:** the hosted worker starts and logs its heartbeat. Its current purpose is demonstration background execution, not ingestion.
- **Frontend scaffold:** Next.js production compilation, TypeScript validation, prerendering, and full ESLint pass. The production home page returns HTTP 200 and renders the starter content.
- **Secret-file exclusion:** deployment/.env and .env.bak are ignored and untracked. Compose sources its password from POSTGRES_PASSWORD rather than a literal. API settings contain no database connection string; the API has a user-secrets identifier. Existing secret values were never printed or committed.

### Implemented foundation, incomplete behavior

- **Identity configuration:** ApplicationUser inherits IdentityUser<Guid>, initializes its ID, and stores a UTC CreatedAt timestamp. DocuMindDbContext inherits IdentityDbContext with Guid user/role keys. Program.cs registers Identity, EF stores, and default token providers.
- **Account policies:** unique email, passwords of at least 12 characters with uppercase/lowercase/digit/symbol requirements, five failed attempts followed by a five-minute lockout, and confirmed email before sign-in are configured. Actual validation, duplicate-email handling, and lockout behavior have not been exercised through an account endpoint. Future login code must opt into failure counting.
- **Cookie configuration:** DocuMind.Auth is HttpOnly, SameSite=Lax, uses HTTPS outside Development, and has a 60-minute ticket with sliding expiration. Authentication precedes authorization; redirect handlers set 401/403. Cookie issuance, persistence, expiry, and protected endpoint behavior are not end-to-end verified because sign-in and protected application routes do not exist.
- **Database readiness:** AddDbContextCheck tests connectivity. It does not verify every table or extension; the separate schema query supplied that evidence for this inspection.
- **pgvector integration:** the extension exists and is declared in the EF model. There are no document/chunk entities, embedding columns, vector type mapping, similarity queries, or vector indexes yet.
- **Docker configuration:** Compose validates and defines only PostgreSQL using pgvector/pgvector:pg17, loopback port 5433 mapped to container port 5432, a named postgres_data volume, and a pg_isready health check. API, worker, and frontend are not containerized here. Live Docker container health and volume attachment could not be inspected because daemon access is denied in this session; direct database connectivity still succeeded.

### Missing from this checkout

- **Registration:** no registration controller, request DTO, application service, or account-creation implementation was found. Services contains only Class1. A read-only GET probe of /api/v1/auth/register returns 404; the stronger evidence is the absence of registration routes and handlers in source and runtime OpenAPI. Previously provided registration code is not implemented in this repository.
- **Account lifecycle:** email confirmation endpoints and delivery, login, logout, current-user endpoint, password reset, and their runtime checks are absent. Token-provider registration alone does not deliver these flows.
- **Frontend integration:** the home page, metadata, and navigation remain Next.js starter content. There are no registration/login forms, document upload/list views, chat UI, or API integration.
- **Browser authentication integration:** no explicit frontend proxy or CORS setup, credentialed API calls, or CSRF protection is implemented. Choose the browser/API origin strategy before integrating cookie-protected state-changing requests.
- **RAG pipeline:** document storage/upload, text extraction, chunking, queueing, embedding generation, vector retrieval, answer generation, citations, and per-user document ownership checks are absent.
- **Automated testing:** no backend test projects or frontend test script/suite were found. Build, lint, EF, metadata queries, and smoke checks are not a replacement for functional tests.

## Package and project inspection

- Installed .NET SDK: **10.0.301**. All projects target **net10.0** with nullable references and implicit usings enabled.
- API: Microsoft.AspNetCore.OpenApi, Microsoft.EntityFrameworkCore.Design, and Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore **10.0.12**. EF Design is marked PrivateAssets=all.
- Data: Microsoft.AspNetCore.Identity.EntityFrameworkCore and Microsoft.EntityFrameworkCore **10.0.12**; Npgsql.EntityFrameworkCore.PostgreSQL **10.0.3**, resolving Npgsql **10.0.3**.
- Worker: Microsoft.Extensions.Hosting **10.0.9**. Its different patch version did not prevent the current build; no package upgrade was necessary.
- Local EF tool manifest: backend/dotnet-tools.json pins dotnet-ef **10.0.12**; the tool runs successfully from backend.
- Frontend manifest and lockfile agree on Next.js/eslint-config-next **16.4.0** and React/React DOM **19.3.0**. The lockfile resolves Tailwind and @tailwindcss/turbopack **4.3.3**, TypeScript **5.9.3**, and ESLint **9.39.5**. Tailwind CSS uses the configured Turbopack loader and builds successfully.
- Resolved backend assets were inspected as well as declared versions. Successful builds demonstrate compatibility in this installed environment; a clean package restore/install and vulnerability audit were not performed.

Official references consulted: [Npgsql EF Core 10 release notes](https://www.npgsql.org/efcore/release-notes/10.0.html), [ASP.NET Core Identity documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity?view=aspnetcore-10.0), and [Next.js font documentation](https://nextjs.org/docs/app/getting-started/fonts).

## Checks and limitations

Passed during this inspection:

- Backend solution build with --no-restore: **four projects, zero warnings, zero errors**.
- Frontend production build, including its TypeScript step, and full ESLint.
- Production frontend HTTP smoke check and worker heartbeat smoke check.
- Compose config --quiet validation via the installed Docker Compose executable (**v2.40.3-desktop.1**).
- API readiness, OpenAPI, and sample controller HTTP smoke checks.
- Read-only PostgreSQL extension/table/migration inspection, EF migrations list, and EF has-pending-model-changes.

Environment limitations and exact scope of runtime evidence:

- The normal API launch failed because this session did not supply ConnectionStrings:DocuMind. This is a local setup requirement; no credential was added to tracked configuration.
- For the successful API and database checks, the existing ignored deployment password was passed only through a temporary process environment, using the configured loopback database port. The probe disabled Windows EventLog logging and used temporary local application-data storage because this restricted session cannot write to the usual locations. It used API port 15185. These overrides were not saved to source or user settings and do not verify the user's normal launch configuration or durable cookie key storage.
- The usual docker compose command could not discover its plugin while Docker's user configuration was inaccessible. Calling the installed plugin directly validated the file. Container inspection still failed with Docker named-pipe access denied. No containers or volumes were recreated or removed.
- EF emitted a warning that no IEntityTypeConfiguration implementations exist in Data. This matches the current scaffold and did not prevent the checks.
- A temporary schema verifier could not restore a new project in this restricted environment. It was compiled using the installed compiler and existing assemblies instead; the subsequent read-only database query passed. No verifier code or credentials were added to the repository.
- No registration, email, login, cookie-session, ownership, or RAG functional tests can pass yet because their application flows are missing. No user accounts were created for this inspection. Smoke-test processes were stopped afterward.

## Small fix made in this step

The frontend initially failed production compilation when next/font/google could not download Geist and Geist Mono. layout.tsx now uses the system font stacks defined in globals.css, removing that build-time network dependency. Tailwind sans/mono tokens use Arial/Helvetica and Courier respectively. This changes typography but leaves starter content and layout intact. Existing dependencies and the lockfile were preserved. Both production build and lint passed after the change.

## Ordered implementation checklist

1. Configure the normal local API connection with user secrets; reproduce readiness and confirm Docker container/volume health from an authorized terminal. Keep the existing database and volume.
2. Implement controller-based registration with a validated request DTO and an injected account service using UserManager. Verify valid registration, invalid passwords, duplicate email, database persistence, and safe error responses against a separate test database.
3. Implement confirmation-token creation and confirmation endpoints plus an email sender abstraction. Keep development email artifacts outside tracked source. Verify confirmation and rejection of invalid/expired tokens.
4. Implement login, logout, current-user, and password reset flows; test confirmed-email enforcement, lockout, 401/403, cookie flags/expiry, and CSRF protections. Add meaningful automated authentication tests.
5. Decide the frontend/API origin strategy, integrate credentialed requests, and replace the starter screen with registration/login and document navigation. Verify browser behavior end to end.
6. Add document ownership, upload/storage, metadata entities and migrations, then worker queueing, extraction, chunking, and embedding persistence. Test authorization and failures before processing real documents.
7. Add vector retrieval and grounded answers with citations, then integrate the Q&A UI and test isolation, retrieval quality, and end-to-end RAG behavior.

## Reproduce the build checks

Run from the repository root in PowerShell with the installed SDK, Node.js, and frontend dependencies:

~~~powershell
# Check all backend projects without changing installed package versions.
dotnet build backend/DocuMind.slnx --no-restore

# Lint and compile the complete frontend, including its TypeScript validation.
Push-Location frontend
npm run lint
npm run build
Pop-Location

# Validate Compose without printing interpolated environment values.
docker compose -f deployment/compose.yaml config --quiet

# Verify the configured API after setting its connection through local user secrets.
dotnet run --project backend/src/DocuMind.Api --no-build --launch-profile http
# In a second terminal, readiness should return Healthy.
Invoke-RestMethod http://localhost:5185/api/v1/health/ready
~~~

Do not run compose down -v or roll back InitialIdentity as part of these checks. Neither action is needed to proceed and both could remove existing data.

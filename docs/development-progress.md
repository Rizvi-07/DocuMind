# DocuMind development progress

Verified on **2026-10-08 (Europe/Berlin)** against the current working tree. Unrelated pre-existing edits remain outside the registration commit. Required Identity/cookie startup wiring was still local and is included as a prerequisite so the committed registration flow works from a fresh checkout. No AGENTS.md instructions were found in the repository or its ancestor directories.

## Actual status

DocuMind has verified registration and email confirmation using Identity, a Development email-preview sender, and a working database foundation. Login and document Q&A remain unimplemented. Registration and confirmation were exercised through the real API against an isolated PostgreSQL database; compilation alone was not treated as proof.

### Complete and verified

- **Solution structure:** all four application projects and the new executable account-flow checker target net10.0 and build. API references Services and Data; Services references Data; Worker references Services and Data. These references compile, but referencing a project does not automatically register its services.
- **PostgreSQL and pgvector:** a read-only query through the existing local connection verified PostgreSQL **17.11** and vector extension **0.8.7**. The API readiness route returned **HTTP 200, Healthy**.
- **Identity schema:** the live database contains AspNetUsers, AspNetRoles, AspNetUserClaims, AspNetRoleClaims, AspNetUserLogins, AspNetUserRoles, and AspNetUserTokens. Migration history contains **20261007065231_InitialIdentity**. EF reports no pending model changes. No migration was applied or rolled back against the existing application database during this step. The flow checker applied InitialIdentity only to its newly created temporary database.
- **API scaffold:** controllers are mapped, development OpenAPI returns HTTP 200, and the sample weather endpoint returns five forecasts. The account controller now exposes registration, confirmation, and resend routes alongside /WeatherForecast. The readiness route is mapped separately.
- **Registration and confirmation:** AccountService in Services uses UserManager for account creation, password validation/hashing, and confirmation. HTTP checks verified unconfirmed accounts, valid confirmation and stored state, invalid input, weak passwords, matching-password validation, generic duplicate responses, and malformed/forged/expired tokens. Registration does not issue a sign-in cookie.
- **Resend and privacy:** valid registration and resend requests return the same generic 202 message. Unknown and confirmed addresses disclose no account state. The checks verified actual previews, recipient cooldown, per-IP 429 responses (including route casing), concurrent duplicates, and unchanged account counts.
- **Email delivery and link configuration:** Development-only JSON previews go to ignored backend/storage/email-previews. Trusted configured application URLs, URL-safe token encoding, token expiration, and production startup refusal without a real sender were tested. No credentials or tokens were committed.
- **Worker scaffold:** the hosted worker starts and logs its heartbeat. Its current purpose is demonstration background execution, not ingestion.
- **Frontend scaffold:** Next.js production compilation, TypeScript validation, prerendering, and full ESLint pass. The production home page returns HTTP 200 and renders the starter content.
- **Secret-file exclusion:** deployment/.env and .env.bak are ignored and untracked. Compose sources its password from POSTGRES_PASSWORD rather than a literal. API settings contain no database connection string; the API has a user-secrets identifier. Existing secret values were never printed or committed.

### Implemented foundation, incomplete behavior

- **Identity configuration:** ApplicationUser inherits IdentityUser<Guid>, initializes its ID, and stores a UTC CreatedAt timestamp. DocuMindDbContext inherits IdentityDbContext with Guid user/role keys. Program.cs registers Identity, EF stores, and default token providers.
- **Account policies:** unique email, passwords of at least 12 characters with uppercase/lowercase/digit/symbol requirements, five failed attempts followed by a five-minute lockout, and confirmed email before sign-in are configured. Registration validation, duplicate handling, and unconfirmed account creation were exercised through the API. Lockout and actual sign-in enforcement remain untested until login exists; future login code must opt into failure counting.
- **Cookie configuration:** DocuMind.Auth is HttpOnly, SameSite=Lax, uses HTTPS outside Development, and has a 60-minute ticket with sliding expiration. Authentication precedes authorization; redirect handlers set 401/403. Cookie issuance, persistence, expiry, and protected endpoint behavior are not end-to-end verified because sign-in and protected application routes do not exist.
- **Database readiness:** AddDbContextCheck tests connectivity. It does not verify every table or extension; the separate schema query supplied that evidence for this inspection.
- **pgvector integration:** the extension exists and is declared in the EF model. There are no document/chunk entities, embedding columns, vector type mapping, similarity queries, or vector indexes yet.
- **Docker configuration:** Compose validates and defines only PostgreSQL using pgvector/pgvector:pg17, loopback port 5433 mapped to container port 5432, a named postgres_data volume, and a pg_isready health check. API, worker, and frontend are not containerized here. Live Docker container health and volume attachment could not be inspected because daemon access is denied in this session; direct database connectivity still succeeded.

### Missing from this checkout

- **Account lifecycle:** login, logout, current-user endpoint, password reset, and their runtime checks remain absent. Confirmation and Development delivery now work; a real production email provider remains unimplemented, and production startup intentionally fails without one.
- **Frontend integration:** the home page, metadata, and navigation remain Next.js starter content. There are no registration/login forms, document upload/list views, chat UI, or API integration.
- **Browser authentication integration:** no explicit frontend proxy or CORS setup, credentialed API calls, or CSRF protection is implemented. Choose the browser/API origin strategy before integrating cookie-protected state-changing requests.
- **RAG pipeline:** document storage/upload, text extraction, chunking, queueing, embedding generation, vector retrieval, answer generation, citations, and per-user document ownership checks are absent.
- **Automated testing:** backend/tests/DocuMind.Auth.FlowChecks now provides repeatable HTTP/PostgreSQL integration verification with no additional test packages. It is an executable checker run with dotnet run, not a dotnet test target. Broader authentication/RAG tests and a frontend test suite remain missing.

## Package and project inspection

- Services now references the installed Microsoft.AspNetCore.App shared framework for Identity token/URL helpers; no package versions were upgraded. The flow checker references API to reuse the existing dependencies.
- Installed .NET SDK: **10.0.301**. All projects target **net10.0** with nullable references and implicit usings enabled.
- API: Microsoft.AspNetCore.OpenApi, Microsoft.EntityFrameworkCore.Design, and Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore **10.0.12**. EF Design is marked PrivateAssets=all.
- Data: Microsoft.AspNetCore.Identity.EntityFrameworkCore and Microsoft.EntityFrameworkCore **10.0.12**; Npgsql.EntityFrameworkCore.PostgreSQL **10.0.3**, resolving Npgsql **10.0.3**.
- Worker: Microsoft.Extensions.Hosting **10.0.9**. Its different patch version did not prevent the current build; no package upgrade was necessary.
- Local EF tool manifest: backend/dotnet-tools.json pins dotnet-ef **10.0.12**; the tool runs successfully from backend.
- Frontend manifest and lockfile agree on Next.js/eslint-config-next **16.4.0** and React/React DOM **19.3.0**. The lockfile resolves Tailwind and @tailwindcss/turbopack **4.3.3**, TypeScript **5.9.3**, and ESLint **9.39.5**. Tailwind CSS uses the configured Turbopack loader and builds successfully.
- Resolved backend assets were inspected as well as declared versions. Successful builds demonstrate compatibility in this installed environment; the solution restore also passed for the new checker in this step. A fresh frontend install and a vulnerability audit were not performed.

Official references consulted: [Npgsql EF Core 10 release notes](https://www.npgsql.org/efcore/release-notes/10.0.html), [ASP.NET Core Identity documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity?view=aspnetcore-10.0), and [Next.js font documentation](https://nextjs.org/docs/app/getting-started/fonts).

## Checks and limitations

Passed during the registration step (frontend, worker, Docker, and original schema evidence below are retained from the preceding status inspection and were not all rerun):

- Backend solution restore and build: **five projects, zero warnings, zero errors**.
- Real registration/confirmation checks on an isolated PostgreSQL database, including hashing, duplicate/concurrent requests, validation, rate limits, resend/cooldown, valid/malformed/forged/expired tokens, and startup guards. The generated database and preview files were cleaned up.
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
- The previous status inspection used an isolated schema verifier after a restricted-runner NuGet failure. This registration step used the authorized SDK runner to restore and build the new flow checker successfully. No production credential or preview email was added to source.
- Registration and confirmation checks created accounts only in a uniquely named temporary database, which was removed afterward. The existing application database and unrelated previews were preserved. Login, authenticated cookies, document ownership, and RAG remain unverified because those flows are missing.

## Previous frontend build fix

The frontend initially failed production compilation when next/font/google could not download Geist and Geist Mono. layout.tsx now uses the system font stacks defined in globals.css, removing that build-time network dependency. Tailwind sans/mono tokens use Arial/Helvetica and Courier respectively. This changes typography but leaves starter content and layout intact. Existing dependencies and the lockfile were preserved. Both production build and lint passed after the change.

## Registration implementation and next steps

AuthController handles HTTP behavior and delegates to IAccountService. Services contains account options, a dedicated Identity confirmation-token provider, and IEmailSender with a guarded Development preview implementation. API configuration validates the trusted link URL, sets rate limits, and resolves delivery at startup. The existing password/unique-email policies and database schema were preserved. Default confirmation lifetime is 24 hours; recipient resend cooldown is 60 seconds.

Delivery is currently synchronous/best-effort with private error logging; a durable outbox is not implemented. Rate limits and cooldowns are local to one process. A deployment with multiple instances needs shared limiting and persisted/shared Data Protection keys. Confirmation is a token-bearing GET link returning JSON; no frontend confirmation screen or automatic sign-in has been added.

See [registration-testing.md](registration-testing.md) for commented PowerShell examples and the repeatable integration checker. The checker requires CREATE DATABASE permission and uses no test accounts in the existing application database.

## Ordered implementation checklist

1. Verify the normal local API connection and application URL through local settings/user secrets; reproduce readiness and inspect Docker health from an authorized terminal while keeping the existing volume.
2. **Completed:** controller-based registration, confirmation, resend, ignored Development previews, validation/privacy/rate limits, and real HTTP/PostgreSQL verification.
3. Implement login, logout, current-user, and password-reset flows. Test confirmed-email enforcement, lockout, cookies, 401/403, and CSRF protection before integrating browser sign-in.
4. Implement a real email provider with secret configuration and reliable delivery; validate persistent/shared Data Protection and multi-instance rate limiting before production deployment.
5. Decide the frontend/API origin strategy and build registration/login/confirmation UI with credentialed requests and browser tests.
6. Add document ownership/upload/storage and metadata migrations, followed by worker queueing, extraction, chunking, embeddings, vector retrieval, and grounded answers with citations. Test isolation and failures throughout.

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

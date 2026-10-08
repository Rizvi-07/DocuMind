# DocuMind development progress

Verified on **2026-10-08 (Europe/Berlin)** for the document-processing/conversation data-model step, building on the verified account/session and unit-testing features. Unrelated pre-existing comment edits and the Class1 rename are preserved separately. No AGENTS.md instructions were found in the repository or its ancestor directories.

## Actual status

DocuMind has verified Identity registration, email confirmation, cookie login/logout, a protected current-user endpoint, and CSRF protection. The Next.js development proxy and browser request helper establish one application origin. Real HTTP checks ran against isolated PostgreSQL databases, including the primary account/session flow through Next.js. The document/chunk/job/conversation/message schema and retry/lease transitions are now verified. Document Q&A services/endpoints, the ingestion worker loop, and account UI remain unimplemented.

### Complete and verified

- **Solution structure:** all four application projects, the executable account/data-flow checkers, and the unit-test project target net10.0 and build (seven projects total). API references Services and Data; Services references Data; Worker references Services and Data. These references compile, but referencing a project does not automatically register its services.
- **PostgreSQL and pgvector:** a read-only query through the existing local connection verified PostgreSQL **17.11** and vector extension **0.8.7**. The API readiness route returned **HTTP 200, Healthy**.
- **Identity schema:** the live database contains AspNetUsers, AspNetRoles, AspNetUserClaims, AspNetRoleClaims, AspNetUserLogins, AspNetUserRoles, and AspNetUserTokens. Migration history now contains **20261007065231_InitialIdentity** and **20261008011434_AddDocumentProcessingAndConversations**. The new migration was inspected and applied only to the identified local documind database/role at 127.0.0.1:5433 after disposable-database checks passed. All rows in all seven Identity tables matched before/after. EF reports no pending migrations or model changes; InitialIdentity was preserved and never rolled back.
- **API scaffold:** controllers are mapped, development OpenAPI returns HTTP 200, and the sample weather endpoint returns five forecasts. The account controller exposes registration, confirmation, resend, csrf, login, logout, and me routes alongside /WeatherForecast. The readiness route is mapped separately.
- **Registration and confirmation:** AccountService in Services uses UserManager for account creation, password validation/hashing, and confirmation. HTTP checks verified unconfirmed accounts, valid confirmation and stored state, invalid input, weak passwords, matching-password validation, generic duplicate responses, and malformed/forged/expired tokens. Registration does not issue a sign-in cookie.
- **Resend and privacy:** valid registration and resend requests return the same generic 202 message. Unknown and confirmed addresses disclose no account state. The checks verified actual previews, recipient cooldown, per-IP 429 responses (including route casing), concurrent duplicates, and unchanged account counts.
- **Email delivery and link configuration:** Development-only JSON previews go to ignored backend/storage/email-previews. Trusted configured application URLs, URL-safe token encoding, token expiration, and production startup refusal without a real sender were tested. No credentials or tokens were committed.
- **Unit testing:** DocuMind.UnitTests is discoverable through dotnet test and IDE test tooling. All **119 backend unit cases** passed for account/session services, controller behavior, request validation, security configuration, and job retry/lease decisions. The preceding unit-testing step passed **18 frontend helper cases** using Node's built-in test runner; frontend checks were not repeated for this Data-only feature. These tests do not require PostgreSQL or a mail provider. See [unit-testing.md](unit-testing.md) for commented commands and scope.
- **Cookie sessions and CSRF:** confirmed-email enforcement, generic authentication failures, Identity lockout counting, successful login, persistent HttpOnly cookies, subsequent authenticated requests, safe current-user fields, logout, and anonymous 401 responses passed real HTTP checks. Missing, forged, and pre-login CSRF tokens were rejected. Email-link GET remains safe; confirmation is a protected POST.
- **Same-origin development:** Next.js rewrites /api/* to the backend during development. Accounts:ApplicationUrl defaults to localhost:3000. The client apiFetch helper obtains fresh CSRF tokens and sends cookies through relative /api/ requests. The main account/session checks passed through the real rewrite.
- **Data model:** documents/conversations have Identity owners; chunks/jobs belong to documents; messages belong to conversations. EF configuration classes enforce ownership FKs, cross-owner association rejection, ordering, safe failure state, page/text/vector constraints, and appropriate indexes. The new PostgreSQL checker passed 40 assertions, including real cosine queries and xmin concurrent-claim/stale-completion conflicts. See [data-model.md](data-model.md) for design and ownership rules.
- **Worker scaffold:** the hosted worker starts and logs its heartbeat. Its current purpose is demonstration background execution, not ingestion.
- **Frontend scaffold:** Next.js production compilation, TypeScript validation, prerendering, and full ESLint pass. The production home page returns HTTP 200 and renders the starter content.
- **Secret-file exclusion:** deployment/.env and .env.bak are ignored and untracked. Compose sources its password from POSTGRES_PASSWORD rather than a literal. API settings contain no database connection string; the API has a user-secrets identifier. Existing secret values were never printed or committed.

### Implemented foundation, incomplete behavior

- **Identity configuration:** ApplicationUser inherits IdentityUser<Guid>, initializes its ID, and stores a UTC CreatedAt timestamp. DocuMindDbContext inherits IdentityDbContext with Guid user/role keys. Program.cs registers Identity, EF stores, and default token providers.
- **Account policies:** unique email, passwords of at least 12 characters with uppercase/lowercase/digit/symbol requirements, five failed attempts followed by a five-minute lockout, and confirmed email before sign-in are configured. Registration validation, duplicate handling, and unconfirmed account creation were exercised through the API. Login now opts into failure counting through SignInManager; unconfirmed sign-in rejection and five-attempt lockout were verified.
- **Cookie configuration:** DocuMind.Auth is HttpOnly, SameSite=Lax, uses HTTPS outside Development, and has a 60-minute ticket with sliding expiration. Authentication precedes authorization; redirect handlers set 401/403. Cookie issuance, persistence across HTTP requests, logout, and protected current-user behavior are verified. HTTPS-only production auth/antiforgery cookie options were inspected through DI. A real production TLS session, 60-minute expiry/sliding-renewal wait, and restart persistence were not tested.
- **Database readiness:** AddDbContextCheck tests connectivity. It does not verify every table or extension; the separate schema query supplied that evidence for this inspection.
- **pgvector integration:** Pgvector.EntityFrameworkCore 0.3.0 maps vector(1536) through the shared UseDocuMindPostgreSql helper. The initial contract is openai/text-embedding-3-small at 1,536 dimensions; database checks reject other model identities, wrong dimensions, and zero vectors. A partial HNSW cosine index and owner-scoped test query work. Actual embedding-provider configuration/calls, question-vector generation, and retrieval services remain unimplemented.
- **Docker configuration:** Compose validates and defines only PostgreSQL using pgvector/pgvector:pg17, loopback port 5433 mapped to container port 5432, a named postgres_data volume, and a pg_isready health check. API, worker, and frontend are not containerized here. Live Docker container health and volume attachment could not be inspected because daemon access is denied in this session; direct database connectivity still succeeded.

### Missing from this checkout

- **Account lifecycle:** password reset, account-management UI, and their runtime checks remain absent. Login/logout/current-user, confirmation, and Development delivery now work. A real production email provider remains unimplemented, and production startup intentionally fails without one.
- **Frontend integration:** the home page, metadata, and navigation remain Next.js starter content. There are no registration/login forms, document upload/list views, or chat UI. The same-origin proxy and API request helper exist but are not yet used by account UI.
- **Production browser deployment:** implement same-origin reverse-proxy routing, trusted forwarded headers, real HTTPS/mail delivery, shared rate limiting, and persistent protected Data Protection keys before deployment. Browser UI automation remains absent. The development origin strategy and CSRF flow are implemented.
- **RAG pipeline:** document storage/upload endpoints, extraction/chunking services, worker polling/heartbeats/recovery scheduling, embedding generation, retrieval services, answer generation, and citations are absent. Durable job transitions and relational ownership constraints now exist. Authenticated ownership enforcement in request services still must be implemented; foreign keys alone do not authorize reads. Future queries must filter by principal-derived OwnerId before lookup/ranking/projection, including message/chunk access through parents.
- **Remaining testing:** backend/tests/DocuMind.Auth.FlowChecks provides real HTTP/PostgreSQL verification through dotnet run, separately from the new dotnet test unit suite and frontend npm test helper suite. Account/session/CSRF/lockout checks are included. The Data flow checker now tests vector/constraint/lease behavior against real PostgreSQL. End-to-end upload/ingestion/chat tests and browser UI automation remain missing because those services/screens are not yet implemented.

## Package and project inspection

- Unit-test-only references: xunit.v3 **3.2.2**, xunit.runner.visualstudio **3.1.5**, Microsoft.NET.Test.Sdk **18.0.1**, and Moq **4.20.72**, all PrivateAssets=all. Official package compatibility and the local restore/build/test verified the net10.0 combination. The frontend adds an npm test script with no new dependency.

- Services now references the installed Microsoft.AspNetCore.App shared framework for Identity token/URL helpers; no package versions were upgraded. The flow checker references API to reuse the existing dependencies.
- Installed .NET SDK: **10.0.301**. All projects target **net10.0** with nullable references and implicit usings enabled.
- API: Microsoft.AspNetCore.OpenApi, Microsoft.EntityFrameworkCore.Design, and Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore **10.0.12**. EF Design is marked PrivateAssets=all.
- Data: Microsoft.AspNetCore.Identity.EntityFrameworkCore and Microsoft.EntityFrameworkCore **10.0.12**; Npgsql.EntityFrameworkCore.PostgreSQL **10.0.3**, resolving Npgsql **10.0.3**. Added **Pgvector.EntityFrameworkCore 0.3.0**, resolving Pgvector **0.3.2**; official adapter documentation supports EF Core 9/10. Existing package versions were not upgraded. References: [pgvector-dotnet](https://github.com/pgvector/pgvector-dotnet), [embedding dimensions](https://developers.openai.com/api/docs/guides/embeddings), [xmin concurrency](https://www.npgsql.org/efcore/modeling/concurrency.html).
- Worker: Microsoft.Extensions.Hosting **10.0.9**. Its different patch version did not prevent the current build; no package upgrade was necessary.
- Local EF tool manifest: backend/dotnet-tools.json pins dotnet-ef **10.0.12**; the tool runs successfully from backend.
- Frontend manifest and lockfile agree on Next.js/eslint-config-next **16.4.0** and React/React DOM **19.3.0**. The lockfile resolves Tailwind and @tailwindcss/turbopack **4.3.3**, TypeScript **5.9.3**, and ESLint **9.39.5**. Tailwind CSS uses the configured Turbopack loader and builds successfully.
- Resolved backend assets were inspected as well as declared versions. Successful builds demonstrate compatibility in this installed environment; the solution restore also passed for the new checker in this step. A fresh frontend install and a vulnerability audit were not performed.

Official references consulted: [Npgsql EF Core 10 release notes](https://www.npgsql.org/efcore/release-notes/10.0.html), [ASP.NET Core Identity documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity?view=aspnetcore-10.0), [Next.js font documentation](https://nextjs.org/docs/app/getting-started/fonts), [SignInManager password sign-in](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.signinmanager-1.passwordsigninasync?view=aspnetcore-10.0), [MVC antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0), and [Next.js rewrites](https://nextjs.org/docs/app/api-reference/config/next-config-js/rewrites).

## Checks and limitations

Passed during this data-model step:

- Seven-project restore/build: **zero warnings/errors**; **119 backend unit cases passed**, zero failed/skipped.
- Disposable PostgreSQL data checker: **40 assertions passed** for migration preservation, ownership/foreign keys, valid round trips, incompatible model/dimension/zero-vector rejection, cosine queries/index creation, text/page/order constraints, cascades, retry backoff, competing claims, interruption recovery, and stale completions.
- Direct HTTP/PostgreSQL account/session checker: **62 assertions passed** after adopting the shared vector mapping and reusing the original private test connection for auxiliary API processes (opened provider connections can redact passwords).
- Migration SQL inspected: five new tables, ten indexes; no Identity table DDL/data changes. Guarded local application: **seven target/history/preservation/model assertions passed**. All Identity rows preserved, no pending migrations/model changes. Temporary test databases/previews cleaned up.
- Frontend, Docker daemon, provider API calls, ingestion/chat HTTP behavior, and production deployment were not tested in this step. See [data-model.md](data-model.md) for commented local commands and exact scope.

Historical evidence from the preceding unit-testing step:

- Solution restore for the new test-only dependencies and complete six-project build: **zero warnings/errors**. Existing application package versions were unchanged.
- Solution-level dotnet test: **109 passed, zero failed/skipped**.
- Frontend helper tests: **18 passed, zero failed/skipped**. Full ESLint and production build/TypeScript validation also passed.
- Unchanged direct HTTP/PostgreSQL integration checker: **62 assertions passed**, using and cleaning up its isolated test database/previews. Existing application data was preserved. The prior Next.js proxy run was not repeated for this test-only step.

The following cookie-authentication evidence is retained from the preceding implementation:

Passed during this cookie-authentication step:

- Backend solution build: **five projects, zero warnings, zero errors**; no dependency/package version change.
- Direct isolated HTTP/PostgreSQL flow checks passed after fixing MVC antiforgery filter service registration.
- Final same-origin run: **62 assertions passed**, with primary registration/confirmation/login/session/logout/CSRF requests sent through a task-owned Next.js development server on unused loopback ports. Auxiliary expiry, lockout, concurrency, and startup guards used separate direct API processes. Generated databases and test previews were cleaned up; existing application data and unrelated previews were preserved.
- Frontend production build, TypeScript validation, prerendering, and full ESLint passed.
- Production auth and antiforgery options were verified as HttpOnly/Secure Always using dependency injection. Production startup still rejects a missing real email sender.

Historical evidence retained from the previous status/registration steps, not all rerun here: solution restore; live PostgreSQL/pgvector/table/migration inspection and EF pending-model check; development OpenAPI/sample weather; worker heartbeat; production frontend HTTP smoke; Compose config validation using v2.40.3-desktop.1. No Docker daemon or production TLS/email-provider verification was added.

Environment limitations and exact scope of runtime evidence:

- The normal API launch failed because this session did not supply ConnectionStrings:DocuMind. This is a local setup requirement; no credential was added to tracked configuration.
- For the successful API and database checks, the existing ignored deployment password was passed only through a temporary process environment, using the configured loopback database port. The probe disabled Windows EventLog logging and used temporary local application-data storage because this restricted session cannot write to the usual locations. It used API port 15185. These overrides were not saved to source or user settings and do not verify the user's normal launch configuration or durable cookie key storage.
- The usual docker compose command could not discover its plugin while Docker's user configuration was inaccessible. Calling the installed plugin directly validated the file. Container inspection still failed with Docker named-pipe access denied. No containers or volumes were recreated or removed.
- The initial status inspection emitted a warning that no IEntityTypeConfiguration implementations existed. This data-model step adds five configuration classes; the current build and migration/model checks pass without that warning.
- The previous status inspection used an isolated schema verifier after a restricted-runner NuGet failure. The registration step used the authorized SDK runner to restore and build the new flow checker successfully. No production credential or preview email was added to source.
- Account-flow checks created accounts only in uniquely named temporary databases, which were removed afterward. Primary sessions and CSRF ran through the real Next.js development proxy; no browser UI automation was performed. The existing application database and unrelated previews were preserved. Relational ownership constraints and a scoped vector query are now verified by the Data checker; HTTP document ownership and RAG remain unverified because those services are missing.

## Previous frontend build fix

The frontend initially failed production compilation when next/font/google could not download Geist and Geist Mono. layout.tsx now uses the system font stacks defined in globals.css, removing that build-time network dependency. Tailwind sans/mono tokens use Arial/Helvetica and Courier respectively. This changes typography but leaves starter content and layout intact. Existing dependencies and the lockfile were preserved. Both production build and lint passed after the change.

## Registration implementation and next steps

AuthController handles HTTP behavior and delegates to IAccountService. Services contains account options, a dedicated Identity confirmation-token provider, and IEmailSender with a guarded Development preview implementation. API configuration validates the trusted link URL, sets rate limits, and resolves delivery at startup. The existing password/unique-email policies and database schema were preserved. Default confirmation lifetime is 24 hours; recipient resend cooldown is 60 seconds.

Delivery is currently synchronous/best-effort with private error logging; a durable outbox is not implemented. Rate limits and cooldowns are local to one process. A deployment with multiple instances needs shared limiting and persisted/shared Data Protection keys. Confirmation links now open a safe GET form; a CSRF-protected JSON/form POST validates the token and changes account state. Login/logout delegate to IIdentitySessionService. The current-user response is an explicit four-field projection. No automatic sign-in or styled Next.js account screen has been added.

See [cookie-authentication-testing.md](cookie-authentication-testing.md) for same-origin setup, endpoint behavior, commented PowerShell session examples, and direct/proxy integration-check commands. [registration-testing.md](registration-testing.md) is updated for the protected confirmation POST. The checker requires CREATE DATABASE permission and uses no test accounts in the existing application database.

## Ordered implementation checklist

1. Verify the normal local API connection and application URL through local settings/user secrets; reproduce readiness and inspect Docker health from an authorized terminal while keeping the existing volume.
2. **Completed:** controller-based registration, confirmation, resend, ignored Development previews, validation/privacy/rate limits, and real HTTP/PostgreSQL verification.
3. **Completed:** cookie login/logout/current-user, confirmed-email enforcement, lockout, generic failures, rate limits, CSRF, same-origin development proxy verification, and focused backend/frontend unit suites. Build registration/login/resend/confirmation UI using apiFetch and add browser tests next.
4. Implement a real email provider with secret configuration and reliable delivery; validate persistent/shared Data Protection and multi-instance rate limiting before production deployment.
5. Implement password-reset/account-management flows with privacy, CSRF, and rate-limit checks; verify expiry, key persistence, and production HTTPS behavior.
6. **Completed:** document/chunk/job/conversation/message models, fixed compatible vector space, ownership relationships/constraints/indexes, retry/recovery transitions, migration inspection/local application with Identity preservation, and unit/PostgreSQL checks.
7. **Next RAG step:** implement authenticated document upload/list/download/delete in Services, private storage with safe generated keys, request size/type validation, and transactional job creation. Enforce principal-derived ownership on every operation and test cross-user isolation.
8. Implement worker claim/heartbeat/recovery scheduling, extraction/chunking, the selected embedding provider, transactional idempotent publication, scoped retrieval, and grounded conversation answers/citations. Test interruptions, retry exhaustion, provider failure, and cross-user isolation throughout.

## Reproduce the build checks

Run from the repository root in PowerShell with the installed SDK, Node.js, and frontend dependencies:

~~~powershell
# Check all backend projects without changing installed package versions.
dotnet build backend/DocuMind.slnx --no-restore

# Run backend unit tests without a database or running API.
dotnet test backend/DocuMind.slnx --no-build --no-restore

# Lint and compile the complete frontend, including its TypeScript validation.
Push-Location frontend
# Check the browser request helper using mocked fetch; no running frontend/API is required.
npm test
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

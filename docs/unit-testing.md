# Unit tests

DocuMind now has fast isolated tests alongside the existing real HTTP/PostgreSQL checker. Backend unit tests use xUnit v3 and Moq; frontend helper tests use Node's built-in test runner and the already-installed TypeScript compiler. No application behavior or database schema was changed for this step.

## What is covered

- AccountService: validation before email-existence checks, private duplicate responses, unconfirmed account creation, trusted confirmation links and URL-safe token round trips, Identity result handling, narrowly handled database uniqueness races, confirmation decoding, eligible resend decisions, shared per-account cooldowns, retry after delivery failure, private logging, and cancellation.
- IdentitySessionService: trimmed email lookup, hashing work for unknown accounts, forwarding rememberMe and lockoutOnFailure=true, uniform unsuccessful Identity results, sign-out delegation, safe current-account projection, and cancellation before dependency calls.
- AuthController: login/logout responses and service delegation, safe current-user JSON, missing-account responses, registration validation feedback and generic accepted messages, resend privacy, JSON/form confirmation outcomes, CSRF request-token responses, and encoded safe email-link forms with restrictive response headers.
- Account request contracts: required fields, email format/length, registration password bounds/matching confirmation, short wrong passwords allowed through login validation, and confirmation-value size limits.
- Authentication configuration: Development versus Production/Staging cookie requirements, HttpOnly and SameSite settings, global antiforgery filter registration, 401/403 redirect handlers, trusted-link URL rules, lifetime/cooldown boundaries, dedicated confirmation token options, and non-Development email-sender guards.
- Frontend apiFetch: unsafe-path rejection, safe methods, protected mutations, fresh identity-bound CSRF request tokens, preserved payload/headers/signals, forced same-origin credentials/no-store caching, unchanged response propagation, and refusing a mutation after failed CSRF bootstrap.

There are five backend test classes plus reusable support doubles under backend/tests/DocuMind.UnitTests. frontend/tests/api.test.mjs tests the actual frontend/src/lib/api.ts implementation through in-memory transpilation. The production frontend build provides the separate TypeScript type check.

## How to read a test

Tests use Arrange / Act / Assert: arrange a dependency's response, invoke the real method, and assert observable behavior. Parameterized Theory cases exercise related outcomes with the same setup; Fact cases cover one behavior. Comments explain the purpose of each test and important decisions.

Strict Identity mocks reject unexpected account/sign-in method calls. Identity constructor properties are allowed explicitly so those implementation details do not prevent mocking. Each account case owns a private memory cache, and controller cases own their HTTP context and service provider; no test shares account/cooldown state. Password inputs are synthetic/generated in memory. No unit test loads deployment/.env, API user secrets, or an application connection string, and mocked email delivery creates no preview files.

These tests isolate application decisions. Direct controller calls do not execute MVC model-binding/authorization/antiforgery filters, rate-limit middleware, or cookie handlers. Mocking a successful SignInManager result does not prove Identity password hashing, confirmation, lockout, token expiration, or cookie issuance works. Those behaviors remain covered by DocuMind.Auth.FlowChecks through the real HTTP pipeline and a separately created PostgreSQL database. No unit tests were added for unchanged template weather/worker examples, empty placeholder classes, or EF-generated migration code.

## Run locally

From D:\DotNetProject\DocuMind, use the installed .NET 10 SDK and frontend Node.js/dependencies:

~~~powershell
# Restore the pinned test-only packages once; application package versions are unchanged.
Set-Location D:\DotNetProject\DocuMind
dotnet restore backend/DocuMind.slnx

# Build all six projects, then run discoverable backend unit tests without starting PostgreSQL.
dotnet build backend/DocuMind.slnx --no-restore
dotnet test backend/DocuMind.slnx --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Backend unit tests failed.' }

# Run one class while learning or debugging its service behavior.
dotnet test backend/tests/DocuMind.UnitTests/DocuMind.UnitTests.csproj --no-build --filter 'FullyQualifiedName~AccountServiceTests'

# Node's test runner mocks fetch; neither the API nor Next.js needs to be running for these tests.
Push-Location frontend
npm test
if ($LASTEXITCODE -ne 0) { throw 'Frontend helper tests failed.' }
npm run lint
npm run build
Pop-Location
~~~

The backend project is discoverable by IDE test tooling through the VSTest adapter. The console integration checker is a separate command and is not run by dotnet test. See [cookie-authentication-testing.md](cookie-authentication-testing.md) for its private connection setup and direct/proxy commands. Do not put test database credentials in a test source file or tracked settings.

## Verified results and limits

Verified on 2026-10-08: **109 backend unit cases and 18 frontend helper cases passed**, with no failures or skips. The complete backend solution built with zero warnings/errors. Frontend ESLint and production compilation/TypeScript validation passed. The unchanged isolated integration checker was rerun directly and passed **62 assertions**, cleaning up its generated database and previews. The earlier same-origin proxy evidence is retained; that proxy run was not repeated for this test-only change.

No browser UI automation, coverage-percentage report, production TLS/email-provider test, durable Data Protection restart test, or live Docker inspection was performed. Tests cover the listed custom behavior; their count does not imply that every line or future feature is tested. Add behavior-focused cases when account rules or frontend screens change.

## Test-only dependencies

DocuMind.UnitTests pins Microsoft.NET.Test.Sdk 18.0.1, xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.5, and Moq 4.20.72 with PrivateAssets=all. xUnit v3 supports .NET 8 and later, including this project's net10.0 target; VSTest enables the normal dotnet test/IDE workflow without switching the entire solution to another test platform. The restore/build/test results verified this package combination in the installed environment. The frontend adds no package dependency.

Official references: [xUnit package compatibility](https://www.nuget.org/packages/xunit.v3/3.2.2), [VSTest adapter](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.5), [Microsoft test SDK](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.0.1), [Moq](https://www.nuget.org/packages/Moq/4.20.72), and [Node test runner](https://nodejs.org/api/test.html).

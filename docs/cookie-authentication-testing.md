# Cookie authentication and same-origin development

IdentitySessionService owns account lookup, SignInManager login/logout, and the safe current-account projection. AuthController owns HTTP status codes, request validation, and the confirmation form. No database migration or password-policy change is needed.

## Endpoint behavior

- GET /api/v1/auth/csrf: anonymous or authenticated callers receive a requestToken and a paired HttpOnly DocuMind.Csrf cookie. Responses are not cached.
- POST /api/v1/auth/login: JSON email, password, and optional rememberMe (default false). A confirmed eligible account returns 204 and DocuMind.Auth. Unknown accounts, wrong passwords, unconfirmed accounts, and locked accounts share the same generic 401 message. Invalid input returns 400. SignInManager counts failed passwords toward the existing five-attempt/five-minute lockout policy.
- GET /api/v1/auth/me: requires authentication; returns only id, email, emailConfirmed, and createdAt. Anonymous callers receive 401 without a login redirect.
- POST /api/v1/auth/logout: requires authentication and CSRF validation; 204 clears the browser authentication cookies. Anonymous callers receive 401.
- Registration, resend, and confirmation POSTs also require CSRF validation. An email-link GET renders a confirmation form without changing account state; submitting its hidden antiforgery token performs confirmation.

All unsafe controller actions require the antiforgery cookie plus X-CSRF-TOKEN request header, or the generated form field for form submissions. A missing, forged, or stale identity-bound token returns 400. Obtain a fresh request token before each mutation, especially after login/logout; never store authentication tokens in localStorage. MVC's built-in antiforgery authorization filter is registered through AddControllersWithViews; only controller routes are mapped.

Login and logout each allow ten requests per IP/action per minute; csrf and me each allow sixty. Registration/resend retain five and confirmation retains twenty. All return 429 with Retry-After on exhaustion. Limits include failed CSRF attempts, use canonical actions to prevent route-casing bypasses, and are local to one process. Behind the development proxy, callers share the proxy's backend IP budget. Production needs a trusted proxy/forwarded-header configuration and shared limits for multiple instances; arbitrary forwarded IP headers are not trusted here.

Authentication cookies remain HttpOnly, SameSite=Lax, and have a 60-minute ticket with sliding expiration. rememberMe=true gives a persistent cookie; false uses a browser-session cookie. Antiforgery cookies are HttpOnly and SameSite=Strict. Both use Secure outside Development; HTTP is permitted locally. Production cookie options were checked through DI, not through a deployed TLS site. Logout removes the browser session; copied authentication tickets are not centrally revoked by this endpoint.

## One browser origin

Open **http://localhost:3000**. Next.js development rewrites forward /api/* to **http://127.0.0.1:5185** server-side. Browser requests, cookies, confirmation links, and the CSRF form all use localhost:3000. Use relative /api/ paths and credentials: same-origin; no cross-origin CORS configuration is needed for this setup. Avoid mixing localhost and 127.0.0.1 in browser URLs.

Accounts:ApplicationUrl defaults to http://localhost:3000 in Development. DOCUMIND_API_ORIGIN is an optional server-only Next.js environment setting for the backend address; it must be a plain HTTP(S) origin. Changing a port requires updating the corresponding setting. The rewrite runs only under next dev; production must route /api/* through its deployment reverse proxy at the same public HTTPS application origin, set Accounts:ApplicationUrl accordingly, and configure a real email provider plus protected persistent Data Protection keys.

frontend/src/lib/api.ts exports apiFetch for client components. It obtains a fresh CSRF token for each mutation and uses browser-managed cookies:

~~~typescript
import { apiFetch } from "@/lib/api";

// In a client event handler, send validated form values; apiFetch supplies CSRF and cookies.
const response = await apiFetch("/api/v1/auth/login", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ email, password, rememberMe: false }),
});
// A 204 response has no JSON body. Render the generic failure if sign-in did not succeed.
if (response.ok) {
  const account = await apiFetch("/api/v1/auth/me");
  // Only the safe account projection is returned; use it to display the signed-in state.
  const currentUser = await account.json();
}
~~~

This step provides the request helper and routing; registration/login UI remains to be built.

## Commented PowerShell testing

Run from D:\DotNetProject\DocuMind. Manual examples create one synthetic account in your configured local database and an ignored preview; the automated checker below uses a separate disposable database instead.

First terminal:

~~~powershell
# Build the actual API and Services code before launching it.
Set-Location D:\DotNetProject\DocuMind
dotnet build backend/DocuMind.slnx

# Skip this prompt if API user secrets already supply the connection; never echo the credential.
$secureConnection = Read-Host 'Local DocuMind PostgreSQL connection string' -AsSecureString
$env:ConnectionStrings__DocuMind = [System.Net.NetworkCredential]::new('', $secureConnection).Password

# Links must target the browser's application origin, rather than the backend port.
$env:Accounts__ApplicationUrl = 'http://localhost:3000'
dotnet run --project backend/src/DocuMind.Api --no-build --launch-profile http
~~~

Second terminal:

~~~powershell
# Next.js proxies /api to this backend address only during development.
Set-Location D:\DotNetProject\DocuMind\frontend
$env:DOCUMIND_API_ORIGIN = 'http://127.0.0.1:5185'
npm run dev -- --port 3000
# Keep this terminal running; use http://localhost:3000 in the browser.
~~~

Third terminal (Windows PowerShell 5.1 or PowerShell 7):

~~~powershell
# One cookie jar models one browser; keep the same origin throughout the flow.
Set-Location D:\DotNetProject\DocuMind
$baseUri = 'http://localhost:3000'
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$email = 'local-' + [Guid]::NewGuid().ToString('N') + '@example.test'
$password = 'Aa9!' + [Guid]::NewGuid().ToString('N')
$registrationJson = @{ email = $email; password = $password; confirmPassword = $password } | ConvertTo-Json
$loginJson = @{ email = $email; password = $password; rememberMe = $false } | ConvertTo-Json

# Fetch the request token paired to this session's HttpOnly cookie and current identity.
function Get-CsrfHeaders {
    $csrf = Invoke-RestMethod -Uri "$baseUri/api/v1/auth/csrf" -WebSession $session
    return @{ 'X-CSRF-TOKEN' = $csrf.requestToken }
}

# Compare status codes without printing response bodies, passwords, or confirmation tokens.
function Assert-ResponseStatus([int]$Expected, [scriptblock]$Request) {
    try {
        $response = & $Request
        $actual = [int]$response.StatusCode
    } catch {
        if ($null -eq $_.Exception.Response) { throw }
        $actual = [int]$_.Exception.Response.StatusCode
    }
    if ($actual -ne $Expected) { throw "Expected HTTP $Expected; received $actual." }
}

# Anonymous access is denied, and registration without CSRF is rejected before creating an account.
Assert-ResponseStatus 401 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/me" -WebSession $session }
Assert-ResponseStatus 400 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/register" -Method Post -WebSession $session -ContentType 'application/json' -Body $registrationJson }
Assert-ResponseStatus 202 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/register" -Method Post -WebSession $session -Headers (Get-CsrfHeaders) -ContentType 'application/json' -Body $registrationJson }

# The correct password still cannot sign in until email ownership is confirmed.
Assert-ResponseStatus 401 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/login" -Method Post -WebSession $session -Headers (Get-CsrfHeaders) -ContentType 'application/json' -Body $loginJson }

# Read only this test account's preview and keep the bearer link private in shell memory.
$preview = Get-ChildItem backend/storage/email-previews -Filter '*.json' | Sort-Object LastWriteTime -Descending | ForEach-Object {
    Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
} | Where-Object { $_.To -eq $email } | Select-Object -First 1
if ($null -eq $preview) { throw 'No preview found for this test account.' }
$link = ($preview.TextBody -split '\r?\n' | Where-Object { $_ -match '^https?://' } | Select-Object -First 1)

# GET is a safe form page; for this script send its link values in a protected JSON POST.
Assert-ResponseStatus 200 { Invoke-WebRequest -UseBasicParsing -Uri $link -WebSession $session }
$query = @{}
foreach ($pair in ([Uri]$link).Query.TrimStart('?').Split('&')) {
    $parts = $pair.Split('=', 2)
    $query[[Uri]::UnescapeDataString($parts[0])] = [Uri]::UnescapeDataString($parts[1])
}
$confirmationJson = @{ userId = $query.userId; token = $query.token } | ConvertTo-Json
Assert-ResponseStatus 200 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/confirm-email" -Method Post -WebSession $session -Headers (Get-CsrfHeaders) -ContentType 'application/json' -Body $confirmationJson }

# Successful login creates the cookie, which authenticates subsequent GET requests.
Assert-ResponseStatus 204 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/login" -Method Post -WebSession $session -Headers (Get-CsrfHeaders) -ContentType 'application/json' -Body $loginJson }
$me = Invoke-RestMethod "$baseUri/api/v1/auth/me" -WebSession $session
if ($me.email -ne $email -or -not $me.emailConfirmed) { throw 'Incorrect current account.' }
Assert-ResponseStatus 200 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/me" -WebSession $session }

# Missing CSRF must not log the account out; a fresh signed-in token then permits logout.
Assert-ResponseStatus 400 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/logout" -Method Post -WebSession $session }
Assert-ResponseStatus 200 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/me" -WebSession $session }
Assert-ResponseStatus 204 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/logout" -Method Post -WebSession $session -Headers (Get-CsrfHeaders) }
Assert-ResponseStatus 401 { Invoke-WebRequest -UseBasicParsing "$baseUri/api/v1/auth/me" -WebSession $session }

# Remove sensitive generated values from shell memory after the checks.
Remove-Variable password, registrationJson, loginJson, confirmationJson, query, preview, link
~~~

To test the email-link experience in a browser, open your test preview link privately at localhost:3000 and submit the generated form instead of sending the JSON confirmation POST. Don't paste bearer links into issues or tracked files. Wait for Retry-After if you exhaust a request budget.

## Unit tests

The backend now also has 109 isolated xUnit cases, and the frontend helper has 18 Node tests. Run dotnet test backend/DocuMind.slnx from the repository root and npm test from frontend. These need no running database, API, or Next.js server; they complement the real cookie/CSRF HTTP checks below. See [unit-testing.md](unit-testing.md) for commented commands, test logic, and exact coverage limits.

## Repeatable integration checks

The console checker launches real API processes, applies InitialIdentity to its own newly generated database, verifies stored account state and cookie sessions, then removes only its generated database/previews/processes. Existing application data is preserved. It requires CREATE DATABASE permission. It is run with dotnet run, not dotnet test.

~~~powershell
# Supply the local test credential privately; it never goes into a tracked file.
Set-Location D:\DotNetProject\DocuMind
$secureTestConnection = Read-Host 'Local PostgreSQL connection with CREATE DATABASE permission' -AsSecureString
$env:DOCUMIND_TEST_CONNECTION = [System.Net.NetworkCredential]::new('', $secureTestConnection).Password

# Default: isolated direct API checks for registration, confirmation, sessions, lockout, CSRF, and limits.
dotnet run --project backend/tests/DocuMind.Auth.FlowChecks
if ($LASTEXITCODE -ne 0) { throw 'Account flow checks failed.' }
Remove-Item Env:DOCUMIND_TEST_CONNECTION
~~~

For repeatable proxy checks, leave the development Next.js process running, reserve an unused backend port (for example 15185), and set DOCUMIND_API_ORIGIN=http://127.0.0.1:15185 in its terminal before starting it. Do not run your ordinary API on this reserved port. In the checker's terminal, set the following alongside the private test connection:

~~~powershell
# The checker owns the API on this reserved port; its browser client and email links use Next.js.
$env:DOCUMIND_FLOW_API_ORIGIN = 'http://127.0.0.1:15185/'
$env:DOCUMIND_FLOW_FRONTEND_ORIGIN = 'http://localhost:3000/'
dotnet run --project backend/tests/DocuMind.Auth.FlowChecks
if ($LASTEXITCODE -ne 0) { throw 'Same-origin proxy checks failed.' }
# Auxiliary API processes still use random ports for independent expiry and lockout budgets.
Remove-Item Env:DOCUMIND_FLOW_API_ORIGIN, Env:DOCUMIND_FLOW_FRONTEND_ORIGIN, Env:DOCUMIND_TEST_CONNECTION
~~~

Verified evidence: 62 assertions passed in the final run, with the primary registration/confirmation/session flow sent through the real Next.js development rewrite. Auxiliary expiry, lockout, concurrency, and startup tests used direct isolated API instances. Frontend TypeScript/production build and ESLint also passed. No browser UI automation, 60-minute expiry wait, durable-key restart test, or deployed production TLS/mail-provider test was performed.

References: [SignInManager password sign-in](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.signinmanager-1.passwordsigninasync?view=aspnetcore-10.0), [MVC antiforgery support](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0), and [Next.js rewrites](https://nextjs.org/docs/app/api-reference/config/next-config-js/rewrites).

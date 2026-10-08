# Registration and email confirmation

The controller handles HTTP validation and status codes. AccountService in Services delegates user creation, password validation/hashing, and confirmation-token validation to ASP.NET Core Identity. Registration never signs the user in.

## Endpoints and responses

- POST /api/v1/auth/register accepts JSON containing email, password, and confirmPassword. Valid new and duplicate requests return the same 202 response. Invalid input returns 400 with validation details.
- GET /api/v1/auth/confirm-email accepts userId and a URL-safe token query and returns a safe confirmation form without updating the account. POST to the same route accepts JSON or the generated form and persists EmailConfirmed=true only after CSRF and Identity token validation. A malformed, forged, expired, or incorrectly bound token returns 400 with an invalid-or-expired-link problem response. Missing/oversized values return 400 through input validation.
- POST /api/v1/auth/resend-confirmation accepts an email. Unknown, already-confirmed, and unconfirmed addresses receive the same 202 response. Only eligible unconfirmed accounts get a message, subject to the recipient cooldown.

All state-changing requests require the antiforgery cookie and X-CSRF-TOKEN header (or the generated hidden form field). Fetch a fresh token from GET /api/v1/auth/csrf in the same cookie session before each POST. See [cookie-authentication-testing.md](cookie-authentication-testing.md) for the complete session flow and same-origin setup.

Registration and resend are each limited to five requests per client IP per minute. Confirmation is limited to twenty per IP per minute. Excess requests return 429 with Retry-After. Limits and recipient cooldowns are held in memory for one API process; a multi-instance deployment needs shared limiting/cooldown storage. Reverse-proxy deployments must configure trusted forwarded headers before rate limiting to obtain the actual client IP.

Responses hide account existence, including delivery failures; response timing is not guaranteed to be indistinguishable. Duplicate registration does not replace the password or resend mail. Provider failures are logged without recipients, tokens, or exception messages and can be retried through resend. This implementation does not include a durable delivery queue.

## Configuration and preview location

Development settings define Accounts:ApplicationUrl as http://localhost:3000, ConfirmationTokenLifetime as 24 hours, and ResendCooldown as 60 seconds. ApplicationUrl must be the public application base URL through which /api is routed, including a deployment path prefix if applicable. Email links use this value, never the request Host header. HTTPS is required outside Development; URL credentials, queries, and fragments are rejected at startup.

The development IEmailSender writes JSON previews to **backend/storage/email-previews/**. The existing backend/storage/ Git ignore rule excludes them. Each preview has To, Subject, and TextBody fields. These local files contain confirmation bearer tokens; they are not served by the API and must stay outside tracked source.

Outside Development, a real DocuMind.Services.Email.IEmailSender must be registered before AddDocuMindAccounts in API Program.cs. Merely setting an email-provider configuration string does not register a provider. The API fails clearly if no sender exists, and resolving a preview sender outside Development is rejected. Provider implementations must validate their own configuration; credentials belong in environment variables or a secret store. Persist and protect the deployment's Data Protection keys so tokens can be validated across restarts and instances. A real provider and styled Next.js confirmation UI remain future work; the API currently serves a minimal protected confirmation form.

## Local testing in PowerShell

Use the repository root, D:\DotNetProject\DocuMind. These examples generate a temporary password in your shell; no real password or connection string is stored in documentation.

Start the API in the first terminal:

~~~powershell
# Move to the checkout and check every backend project, including the executable flow checks.
Set-Location D:\DotNetProject\DocuMind
dotnet build backend/DocuMind.slnx

# Supply the existing local connection without echoing it. Skip this if user secrets already supply it.
$secureConnection = Read-Host 'Local DocuMind PostgreSQL connection string' -AsSecureString
$env:ConnectionStrings__DocuMind = [System.Net.NetworkCredential]::new('', $secureConnection).Password

# Email links use the Next.js browser origin; its development proxy forwards /api to port 5185.
$env:Accounts__ApplicationUrl = 'http://localhost:3000'
dotnet run --project backend/src/DocuMind.Api --no-build --launch-profile http
~~~

Start Next.js in a second terminal as shown in [cookie-authentication-testing.md](cookie-authentication-testing.md). In a third terminal, create and confirm a local test account:

~~~powershell
# Use a unique synthetic address and generate a password satisfying the existing Identity policy.
Set-Location D:\DotNetProject\DocuMind
$baseUri = 'http://localhost:3000'
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()

# Get a new identity-bound request token using the same cookie jar for every mutation.
function Get-RegistrationCsrfHeaders {
    $csrf = Invoke-RestMethod -Uri "$baseUri/api/v1/auth/csrf" -WebSession $session
    return @{ 'X-CSRF-TOKEN' = $csrf.requestToken }
}
$email = 'local-' + [Guid]::NewGuid().ToString('N') + '@example.test'
$password = 'Aa9!' + [Guid]::NewGuid().ToString('N')
$body = @{ email = $email; password = $password; confirmPassword = $password } | ConvertTo-Json

# Registration returns 202; it deliberately does not report whether an account was created.
$registered = Invoke-WebRequest -UseBasicParsing -Uri "$baseUri/api/v1/auth/register" -Method Post -WebSession $session -Headers (Get-RegistrationCsrfHeaders) -ContentType 'application/json' -Body $body
$registered.StatusCode

# Duplicate registration must have the same status and body and leave the original password unchanged.
$duplicate = Invoke-WebRequest -UseBasicParsing -Uri "$baseUri/api/v1/auth/register" -Method Post -WebSession $session -Headers (Get-RegistrationCsrfHeaders) -ContentType 'application/json' -Body $body
if ($duplicate.StatusCode -ne 202 -or $duplicate.Content -ne $registered.Content) { throw 'Duplicate response differs.' }

# Read only the preview addressed to this shell-generated test account; do not print its token.
$preview = Get-ChildItem backend/storage/email-previews -Filter '*.json' | Sort-Object LastWriteTime -Descending | ForEach-Object {
    Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
} | Where-Object { $_.To -eq $email } | Select-Object -First 1
$link = ($preview.TextBody -split '\r?\n' | Where-Object { $_ -match '^https?://' } | Select-Object -First 1)
# Visiting a link displays the safe form; this script submits the same values as protected JSON.
Invoke-WebRequest -UseBasicParsing -Uri $link -WebSession $session | Out-Null
$query = @{}
foreach ($pair in ([Uri]$link).Query.TrimStart('?').Split('&')) {
    $parts = $pair.Split('=', 2)
    $query[[Uri]::UnescapeDataString($parts[0])] = [Uri]::UnescapeDataString($parts[1])
}
$confirmationBody = @{ userId = $query.userId; token = $query.token } | ConvertTo-Json
Invoke-RestMethod -Uri "$baseUri/api/v1/auth/confirm-email" -Method Post -WebSession $session -Headers (Get-RegistrationCsrfHeaders) -ContentType 'application/json' -Body $confirmationBody

# Resend always returns a generic response; confirmed accounts receive no additional message.
$resendBody = @{ email = $email } | ConvertTo-Json
Invoke-RestMethod -Uri "$baseUri/api/v1/auth/resend-confirmation" -Method Post -WebSession $session -Headers (Get-RegistrationCsrfHeaders) -ContentType 'application/json' -Body $resendBody

# Invalid input must return 400; the response includes validation errors rather than account information.
$invalidBody = @{ email = 'not-an-email'; password = $password; confirmPassword = $password } | ConvertTo-Json
try {
    Invoke-WebRequest -UseBasicParsing -Uri "$baseUri/api/v1/auth/register" -Method Post -WebSession $session -Headers (Get-RegistrationCsrfHeaders) -ContentType 'application/json' -Body $invalidBody | Out-Null
    throw 'Expected invalid registration to return 400.'
} catch {
    if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 400) { throw }
}

# A forged token cannot confirm an account and must return 400 with an invalid-or-expired-link response.
try {
    $unknownId = [Guid]::NewGuid().ToString()
    $forgedBody = @{ userId = $unknownId; token = 'invalid' } | ConvertTo-Json
    Invoke-WebRequest -UseBasicParsing -Uri "$baseUri/api/v1/auth/confirm-email" -Method Post -WebSession $session -Headers (Get-RegistrationCsrfHeaders) -ContentType 'application/json' -Body $forgedBody | Out-Null
    throw 'Expected an invalid confirmation token to return 400.'
} catch {
    if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 400) { throw }
}

# Remove the generated password from this shell once it is no longer needed.
Remove-Variable password, body, preview, link, query, confirmationBody
~~~

To inspect resend delivery for an unconfirmed account, use a new generated address, leave its link unopened, wait for the 60-second recipient cooldown, and request resend. Repeated calls during that cooldown still return 202 but write no extra preview. If you reach 429, wait for the indicated retry interval before continuing.

## Repeatable automated verification

DocuMind.Auth.FlowChecks is an executable integration checker, not an xUnit project or a dotnet test target. It requires a PostgreSQL role permitted to create/drop a database and pgvector availability. It creates a uniquely named database, applies InitialIdentity there, launches the real API on free ports, and verifies HTTP responses plus stored Identity state. Its generated database, API processes, test previews, and temporary key files are cleaned up afterward. The supplied application's tables and unrelated previews are preserved.

~~~powershell
# Provide an administrative local connection privately; it is used only to create an isolated test database.
Set-Location D:\DotNetProject\DocuMind
$secureTestConnection = Read-Host 'Local PostgreSQL connection with CREATE DATABASE permission' -AsSecureString
$env:DOCUMIND_TEST_CONNECTION = [System.Net.NetworkCredential]::new('', $secureTestConnection).Password

# Build and run actual HTTP/database checks: creation, hashing, duplicates, validation, resend, rate limits,
# confirmation, malformed/forged/expired tokens, concurrent duplicates, session cookies, CSRF, and startup guards.
dotnet run --project backend/tests/DocuMind.Auth.FlowChecks
if ($LASTEXITCODE -ne 0) { throw 'Registration flow checks failed.' }

# Remove the test credential from this shell after verification.
Remove-Item Env:DOCUMIND_TEST_CONNECTION
~~~

Official implementation references: [Identity account confirmation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/accconfirm?view=aspnetcore-10.0), [ASP.NET Core rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0), and [startup options validation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options?view=aspnetcore-10.0).

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DocuMind.Api.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using System.Text.Json;
using DocuMind.Data;
using DocuMind.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Run against a uniquely named temporary database, never the supplied application's tables.
return await RegistrationFlowChecks.RunAsync();

/// <summary>Exercises the real HTTP endpoints, Identity storage, previews, and startup guards.</summary>
internal static class RegistrationFlowChecks
{
    private static readonly List<Process> ApiProcesses = [];
    private static readonly HashSet<string> TestEmails = new(StringComparer.OrdinalIgnoreCase);
    private static string repositoryRoot = string.Empty;
    private static string previewDirectory = string.Empty;
    private static string temporaryDirectory = string.Empty;

    /// <summary>Creates an isolated database and cleans up only resources created by this run.</summary>
    public static async Task<int> RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("DOCUMIND_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Set DOCUMIND_TEST_CONNECTION through your local environment. Its role must be able to create a test database.");
            return 1;
        }

        var databaseName = "documind_auth_verify_" + Guid.NewGuid().ToString("N");
        var databaseCreated = false;
        await using var admin = new NpgsqlConnection(connectionString);
        try
        {
            repositoryRoot = FindRepositoryRoot();
            previewDirectory = Path.Combine(repositoryRoot, "backend", "storage", "email-previews");
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "documind-auth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            await admin.OpenAsync();

            // The identifier is generated locally from a fixed prefix plus a GUID, not user input.
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            {
                await create.ExecuteNonQueryAsync();
                databaseCreated = true;
            }

            var testConnection = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
            var databaseOptions = new DbContextOptionsBuilder<DocuMindDbContext>().UseDocuMindPostgreSql(testConnection).Options;
            await using var database = new DocuMindDbContext(databaseOptions);
            await database.Database.MigrateAsync();

            // Passwords and test email addresses are generated in memory and never written to tracked files.
            var password = "Aa9!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var email = NewEmail();
            var api = await StartApiAsync(testConnection, useFrontend: true);
            using var client = new HttpClient { BaseAddress = api.Address };
            var registration = new { email, password, confirmPassword = password };
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/register")
            {
                Content = JsonContent.Create(registration)
            };
            request.Headers.Host = "untrusted.example";
            request.Headers.Add(BrowserAuthenticationConfiguration.CsrfHeaderName, await GetCsrfTokenAsync(client));
            using var registered = await client.SendAsync(request);
            var acceptedBody = await registered.Content.ReadAsStringAsync();
            Assert(registered.StatusCode == HttpStatusCode.Accepted, "Successful registration returns 202.");
            Assert(!registered.Headers.Contains("Set-Cookie"), "Registration does not create a signed-in cookie.");
            var user = await database.Users.AsNoTracking().SingleAsync(account => account.Email == email);
            Assert(!user.EmailConfirmed, "New accounts are unconfirmed.");
            Assert(user.PasswordHash is not null && user.PasswordHash != password
                && new PasswordHasher<ApplicationUser>().VerifyHashedPassword(user, user.PasswordHash, password)
                    != PasswordVerificationResult.Failed, "Identity stores a verifiable password hash.");
            var originalLink = ReadPreviewLinks(email).Single();
            var parsedLink = new Uri(originalLink);
            var query = QueryHelpers.ParseQuery(parsedLink.Query);
            Assert(parsedLink.Authority == api.Address.Authority
                && query["token"].ToString().All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'),
                "Confirmation links use the configured origin and URL-safe encoding, even with a spoofed Host header.");

            using var duplicate = await PostProtectedAsync(client, "api/v1/auth/register",
                new { email, password = password + "Changed", confirmPassword = password + "Changed" });
            Assert(duplicate.StatusCode == HttpStatusCode.Accepted
                && await duplicate.Content.ReadAsStringAsync() == acceptedBody,
                "Duplicate registration has the same status and body.");
            Assert(await database.Users.CountAsync(account => account.NormalizedEmail == user.NormalizedEmail) == 1
                && ReadPreviewLinks(email).Count == 1, "Duplicate registration creates no second account or email.");
            Assert(await database.Users.AsNoTracking().Where(account => account.Id == user.Id)
                .Select(account => account.PasswordHash).SingleAsync() == user.PasswordHash,
                "Duplicate registration never changes the existing password hash.");

            using var invalidEmail = await PostProtectedAsync(client, "api/v1/auth/register",
                new { email = "not-an-email", password, confirmPassword = password });
            Assert(invalidEmail.StatusCode == HttpStatusCode.BadRequest, "Invalid email input returns 400.");
            var weakPassword = new string('a', 16);
            using var weak = await PostProtectedAsync(client, "api/v1/auth/register",
                new { email = NewEmail(), password = weakPassword, confirmPassword = weakPassword });
            Assert(weak.StatusCode == HttpStatusCode.BadRequest, "Identity password strength rules return 400.");
            using var mismatch = await PostProtectedAsync(client, "api/v1/auth/register",
                new { email = NewEmail(), password, confirmPassword = password + "x" });
            Assert(mismatch.StatusCode == HttpStatusCode.BadRequest, "Password confirmation mismatch returns 400.");
            Assert(await database.Users.CountAsync() == 1, "Invalid registration requests persist no accounts.");
            using var rateLimited = await PostProtectedAsync(client, "API/V1/AUTH/REGISTER/", registration);
            Assert(rateLimited.StatusCode == HttpStatusCode.TooManyRequests
                && rateLimited.Headers.RetryAfter is not null, "Registration enforces its five-per-minute IP limit even with route casing changes.");

            using var unknownResend = await PostProtectedAsync(client, "api/v1/auth/resend-confirmation", new { email = NewEmail() });
            Assert(unknownResend.StatusCode == HttpStatusCode.Accepted
                && await unknownResend.Content.ReadAsStringAsync() == acceptedBody,
                "Unknown-email resend preserves the generic response.");
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            using var resend = await PostProtectedAsync(client, "api/v1/auth/resend-confirmation", new { email });
            Assert(resend.StatusCode == HttpStatusCode.Accepted
                && await resend.Content.ReadAsStringAsync() == acceptedBody && ReadPreviewLinks(email).Count == 2,
                "Resend writes another preview for an unconfirmed account.");
            using var immediateResend = await PostProtectedAsync(client, "api/v1/auth/resend-confirmation", new { email });
            Assert(immediateResend.StatusCode == HttpStatusCode.Accepted && ReadPreviewLinks(email).Count == 2,
                "Recipient cooldown prevents immediate repeated delivery.");

            using var malformed = await PostProtectedAsync(client, "api/v1/auth/confirm-email",
                new { userId = user.Id.ToString(), token = "***" });
            Assert(malformed.StatusCode == HttpStatusCode.BadRequest
                && (await malformed.Content.ReadAsStringAsync()).Contains("Invalid or expired"),
                "Malformed confirmation tokens return a clear 400.");
            var forgedToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
            using var forged = await PostProtectedAsync(client, "api/v1/auth/confirm-email",
                new { userId = user.Id.ToString(), token = forgedToken });
            Assert(forged.StatusCode == HttpStatusCode.BadRequest, "Well-encoded forged tokens cannot confirm an account.");
            Assert(!await database.Users.AsNoTracking().Where(account => account.Id == user.Id)
                .Select(account => account.EmailConfirmed).SingleAsync(), "Invalid tokens leave the account unconfirmed.");
            await VerifyUnconfirmedLoginAsync(client, email, password);
            using var landing = await client.GetAsync(originalLink);
            Assert(landing.StatusCode == HttpStatusCode.OK
                && !await database.Users.AsNoTracking().Where(account => account.Id == user.Id)
                    .Select(account => account.EmailConfirmed).SingleAsync(),
                "Email-link GET renders a safe form and leaves the account unconfirmed.");
            using var confirmed = await SubmitConfirmationFormAsync(client, originalLink);
            Assert(confirmed.StatusCode == HttpStatusCode.OK
                && confirmed.Headers.CacheControl?.NoStore == true,
                "A valid token confirms the email and disables response caching.");
            Assert(await database.Users.AsNoTracking().Where(account => account.Id == user.Id)
                .Select(account => account.EmailConfirmed).SingleAsync(), "Confirmation persists in PostgreSQL.");
            using var confirmedResend = await PostProtectedAsync(client, "api/v1/auth/resend-confirmation", new { email });
            Assert(confirmedResend.StatusCode == HttpStatusCode.Accepted
                && await confirmedResend.Content.ReadAsStringAsync() == acceptedBody && ReadPreviewLinks(email).Count == 2,
                "Confirmed-account resend stays generic and sends no email.");

            using var fifthResend = await PostProtectedAsync(client, "api/v1/auth/resend-confirmation", new { email = NewEmail() });
            Assert(fifthResend.StatusCode == HttpStatusCode.Accepted, "Resend accepts requests within its IP limit.");
            using var limitedResend = await PostProtectedAsync(client, "api/v1/auth/resend-confirmation", new { email });
            Assert(limitedResend.StatusCode == HttpStatusCode.TooManyRequests, "Resend also enforces its per-IP rate limit.");

            await VerifyCookieSessionsAsync(client, database, user, email, password, testConnection);
            VerifyProductionCookieOptions();

            // A second instance gives expiry an actual short lifetime, without waiting 24 hours.
            var expiringApi = await StartApiAsync(testConnection, lifetime: "00:00:01");
            using var expiringClient = new HttpClient { BaseAddress = expiringApi.Address };
            var expiringEmail = NewEmail();
            using var expiringRegistration = await PostProtectedAsync(expiringClient, "api/v1/auth/register",
                new { email = expiringEmail, password, confirmPassword = password });
            Assert(expiringRegistration.StatusCode == HttpStatusCode.Accepted, "Expiry test account is created.");
            var expiringLink = ReadPreviewLinks(expiringEmail).Single();
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            var expiredQuery = QueryHelpers.ParseQuery(new Uri(expiringLink).Query);
            using var expired = await PostProtectedAsync(expiringClient, "api/v1/auth/confirm-email",
                new { userId = expiredQuery["userId"].ToString(), token = expiredQuery["token"].ToString() });
            Assert(expired.StatusCode == HttpStatusCode.BadRequest
                && (await expired.Content.ReadAsStringAsync()).Contains("Invalid or expired"),
                "Identity rejects an expired confirmation token with a clear 400.");
            Assert(!await database.Users.AsNoTracking().Where(account => account.Email == expiringEmail)
                .Select(account => account.EmailConfirmed).SingleAsync(), "Expired tokens leave the account unconfirmed.");

            // Concurrent requests exercise the database uniqueness race rather than just a precheck.
            var raceEmail = NewEmail();
            var raceResponses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                PostProtectedAsync(expiringClient, "api/v1/auth/register",
                    new { email = raceEmail, password, confirmPassword = password })));
            foreach (var response in raceResponses)
            {
                using (response)
                {
                    Assert(response.StatusCode == HttpStatusCode.Accepted
                        && await response.Content.ReadAsStringAsync() == acceptedBody,
                        "Concurrent duplicates preserve the accepted response.");
                }
            }
            Assert(await database.Users.CountAsync(account => account.Email == raceEmail) == 1,
                "Concurrent duplicate registration creates exactly one account.");

            using var caseDuplicate = await PostProtectedAsync(expiringClient, "api/v1/auth/register",
                new { email = email.ToUpperInvariant(), password, confirmPassword = password });
            Assert(caseDuplicate.StatusCode == HttpStatusCode.Accepted
                && await caseDuplicate.Content.ReadAsStringAsync() == acceptedBody
                && await database.Users.CountAsync(account => account.NormalizedEmail == user.NormalizedEmail) == 1,
                "Email uniqueness and duplicate privacy are case-insensitive.");

            await AssertStartupFailureAsync(testConnection, "Production", "https://documind.example",
                "No production email sender is configured", "Production refuses to start without a real email sender.");
            await AssertStartupFailureAsync(testConnection, "Development", "not-an-absolute-url",
                "Accounts:ApplicationUrl", "Invalid application URLs fail startup validation.");
            Console.WriteLine("All registration, confirmation, cookie-session, and CSRF flow checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            // Assertion messages are controlled. Never dump exception details that might contain a token or connection string.
            Console.Error.WriteLine(exception is FlowCheckException ? exception.Message
                : $"Flow verification failed ({exception.GetType().Name}); no credentials or tokens were printed.");
            return 1;
        }
        finally
        {
            foreach (var process in ApiProcesses)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }

            // Remove only previews addressed to this run's generated accounts; leave all other previews untouched.
            if (Directory.Exists(previewDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(previewDirectory, "*.json"))
                {
                    using var preview = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                    if (TestEmails.Contains(preview.RootElement.GetProperty("To").GetString()!)) File.Delete(path);
                }
            }

            if (databaseCreated)
            {
                // Only the generated database can reach this statement. Never drop the supplied database.
                NpgsqlConnection.ClearAllPools();
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }

            if (!string.IsNullOrEmpty(temporaryDirectory) && Directory.Exists(temporaryDirectory))
            {
                // Recheck the resolved target before recursive deletion on Windows.
                var target = Path.GetFullPath(temporaryDirectory);
                var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(target), expectedParent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(target).StartsWith("documind-auth-", StringComparison.Ordinal))
                {
                    throw new FlowCheckException("Refusing to delete a temporary directory outside the expected location.");
                }
                Directory.Delete(target, recursive: true);
            }
        }
    }

    /// <summary>Pairs the HttpOnly CSRF cookie with a fresh token for the client's current identity.</summary>
    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("api/v1/auth/csrf");
        if (!response.IsSuccessStatusCode) throw new FlowCheckException($"CSRF bootstrap must succeed (received {(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("requestToken").GetString()!;
    }

    /// <summary>Sends a real protected JSON mutation; no controller validation is bypassed for tests.</summary>
    private static async Task<HttpResponseMessage> PostProtectedAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(BrowserAuthenticationConfiguration.CsrfHeaderName, await GetCsrfTokenAsync(client));
        // Mimic same-origin browser fetch headers, including when Next.js forwards to a different backend port.
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return await client.SendAsync(request);
    }

    /// <summary>Follows the email-link form using its own hidden antiforgery token and cookie pair.</summary>
    private static async Task<HttpResponseMessage> SubmitConfirmationFormAsync(HttpClient client, string link)
    {
        var html = await client.GetStringAsync(link);
        var fields = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
            .Select(match => new KeyValuePair<string, string>(WebUtility.HtmlDecode(match.Groups[1].Value),
                WebUtility.HtmlDecode(match.Groups[2].Value))).ToArray();
        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync("api/v1/auth/confirm-email", content);
    }

    /// <summary>Checks anonymous authorization and confirmed-email enforcement before confirmation occurs.</summary>
    private static async Task VerifyUnconfirmedLoginAsync(HttpClient client, string email, string password)
    {
        using var anonymous = await client.GetAsync("api/v1/auth/me");
        Assert(anonymous.StatusCode == HttpStatusCode.Unauthorized && anonymous.Headers.Location is null,
            "Anonymous current-user access returns 401 rather than a login redirect.");
        using var unconfirmed = await PostProtectedAsync(client, "api/v1/auth/login", new { email, password });
        Assert(unconfirmed.StatusCode == HttpStatusCode.Unauthorized && !unconfirmed.Headers.Contains("Set-Cookie"),
            "Unconfirmed accounts cannot sign in or receive an authentication cookie.");
    }

    /// <summary>Verifies cookie sessions, CSRF enforcement, safe projections, lockout, and rate limiting through HTTP.</summary>
    private static async Task VerifyCookieSessionsAsync(HttpClient client, DocuMindDbContext database,
        ApplicationUser user, string email, string password, string testConnection)
    {
        // Raw requests deliberately omit the required header to prove validation is enforced.
        using var missingLogin = await client.PostAsJsonAsync("api/v1/auth/login", new { email, password });
        Assert(missingLogin.StatusCode == HttpStatusCode.BadRequest, "Login without a CSRF token is rejected.");
        using var unknown = await PostProtectedAsync(client, "api/v1/auth/login", new { email = NewEmail(), password });
        var genericFailure = await unknown.Content.ReadAsStringAsync();
        var incorrectPassword = password + "wrong";
        using var incorrect = await PostProtectedAsync(client, "api/v1/auth/login", new { email, password = incorrectPassword });
        Assert(unknown.StatusCode == HttpStatusCode.Unauthorized && incorrect.StatusCode == HttpStatusCode.Unauthorized
            && await incorrect.Content.ReadAsStringAsync() == genericFailure,
            "Unknown accounts and incorrect passwords share one generic authentication failure.");
        Assert(await database.Users.AsNoTracking().Where(account => account.Id == user.Id)
            .Select(account => account.AccessFailedCount).SingleAsync() == 1,
            "An incorrect password counts toward the configured lockout policy.");
        var anonymousToken = await GetCsrfTokenAsync(client);
        using var signedIn = await PostProtectedAsync(client, "api/v1/auth/login", new { email, password, rememberMe = true });
        Assert(signedIn.StatusCode == HttpStatusCode.NoContent, "A confirmed account signs in successfully.");
        var authCookie = signedIn.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("DocuMind.Auth=", StringComparison.Ordinal));
        Assert(authCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)
            && authCookie.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase)
            && authCookie.Contains("expires=", StringComparison.OrdinalIgnoreCase),
            "Login issues an HttpOnly SameSite=Lax persistent cookie when requested.");
        using var me = await client.GetAsync("api/v1/auth/me");
        using var account = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        var fields = account.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert(me.StatusCode == HttpStatusCode.OK && account.RootElement.GetProperty("id").GetGuid() == user.Id
            && fields.SequenceEqual(new[] { "createdAt", "email", "emailConfirmed", "id" }),
            "Current-user returns only the authenticated account's four safe fields.");
        Assert(me.Headers.CacheControl?.NoStore == true, "Current-user responses are not cached.");
        using var persisted = await client.GetAsync("api/v1/auth/me");
        Assert(persisted.StatusCode == HttpStatusCode.OK, "The cookie authenticates subsequent requests.");
        using var rawLogout = await client.PostAsync("api/v1/auth/logout", null);
        Assert(rawLogout.StatusCode == HttpStatusCode.BadRequest, "Authenticated logout without a CSRF token is rejected.");
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/logout");
        invalidRequest.Headers.Add(BrowserAuthenticationConfiguration.CsrfHeaderName, "forged-token");
        using var invalidLogout = await client.SendAsync(invalidRequest);
        Assert(invalidLogout.StatusCode == HttpStatusCode.BadRequest, "A forged CSRF token is rejected.");
        using var staleRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/logout");
        staleRequest.Headers.Add(BrowserAuthenticationConfiguration.CsrfHeaderName, anonymousToken);
        using var staleLogout = await client.SendAsync(staleRequest);
        Assert(staleLogout.StatusCode == HttpStatusCode.BadRequest, "A pre-login CSRF token cannot mutate a signed-in session.");
        using var stillSignedIn = await client.GetAsync("api/v1/auth/me");
        Assert(stillSignedIn.StatusCode == HttpStatusCode.OK, "Rejected logout attempts preserve the session.");
        using var loggedOut = await PostProtectedAsync(client, "api/v1/auth/logout", new { });
        Assert(loggedOut.StatusCode == HttpStatusCode.NoContent, "Protected logout succeeds.");
        using var afterLogout = await client.GetAsync("api/v1/auth/me");
        Assert(afterLogout.StatusCode == HttpStatusCode.Unauthorized, "Logout clears browser authentication.");

        // Start fresh rate-limit counters so five wrong passwords can be tested without waiting a minute.
        // Reuse the private original configuration: an opened provider connection may redact its password.
        var separateApi = await StartApiAsync(testConnection);
        using var lockoutClient = new HttpClient { BaseAddress = separateApi.Address };
        using var unprotectedRegister = await lockoutClient.PostAsJsonAsync("api/v1/auth/register",
            new { email = NewEmail(), password, confirmPassword = password });
        Assert(unprotectedRegister.StatusCode == HttpStatusCode.BadRequest, "Registration requires CSRF even for anonymous callers.");
        using var unprotectedResend = await lockoutClient.PostAsJsonAsync("api/v1/auth/resend-confirmation", new { email });
        Assert(unprotectedResend.StatusCode == HttpStatusCode.BadRequest, "Resend requires CSRF protection.");
        using var unprotectedConfirm = await lockoutClient.PostAsJsonAsync("api/v1/auth/confirm-email",
            new { userId = user.Id.ToString(), token = "invalid" });
        Assert(unprotectedConfirm.StatusCode == HttpStatusCode.BadRequest, "Confirmation mutations require CSRF protection.");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failure = await PostProtectedAsync(lockoutClient, "api/v1/auth/login", new { email, password = incorrectPassword });
            Assert(failure.StatusCode == HttpStatusCode.Unauthorized
                && await failure.Content.ReadAsStringAsync() == genericFailure, "Failed passwords keep the generic 401 response.");
        }
        var lockoutEnd = await database.Users.AsNoTracking().Where(account => account.Id == user.Id)
            .Select(account => account.LockoutEnd).SingleAsync();
        Assert(lockoutEnd > DateTimeOffset.UtcNow, "Five failed passwords trigger the existing temporary lockout.");
        using var locked = await PostProtectedAsync(lockoutClient, "api/v1/auth/login", new { email, password });
        Assert(locked.StatusCode == HttpStatusCode.Unauthorized && await locked.Content.ReadAsStringAsync() == genericFailure,
            "Locked accounts reject correct passwords without disclosing the reason.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var ignored = await PostProtectedAsync(lockoutClient, "api/v1/auth/login", new { email, password });
        }
        using var limited = await PostProtectedAsync(lockoutClient, "API/V1/AUTH/LOGIN/", new { email, password });
        Assert(limited.StatusCode == HttpStatusCode.TooManyRequests && limited.Headers.RetryAfter is not null,
            "Login rate limits cannot be bypassed through route casing.");
    }

    /// <summary>Inspects production option registrations without enabling a fake production mail provider.</summary>
    private static void VerifyProductionCookieOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocuMindBrowserAuthentication(new HostingEnvironment { EnvironmentName = Environments.Production });
        using var provider = services.BuildServiceProvider();
        var auth = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var csrf = provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        Assert(auth.Cookie.HttpOnly && auth.Cookie.SecurePolicy == Microsoft.AspNetCore.Http.CookieSecurePolicy.Always
            && csrf.Cookie.HttpOnly && csrf.Cookie.SecurePolicy == Microsoft.AspNetCore.Http.CookieSecurePolicy.Always,
            "Production authentication and antiforgery cookies are HttpOnly and HTTPS-only.");
    }

    /// <summary>Starts a real API process with test-only configuration and waits for readiness.</summary>
    private static async Task<(Process Process, Uri Address)> StartApiAsync(string connectionString,
        string lifetime = "1.00:00:00", bool useFrontend = false)
    {
        var address = new Uri(useFrontend
            ? Environment.GetEnvironmentVariable("DOCUMIND_FLOW_API_ORIGIN") ?? $"http://127.0.0.1:{FindAvailablePort()}/"
            : $"http://127.0.0.1:{FindAvailablePort()}/");
        var publicAddress = useFrontend
            ? new Uri(Environment.GetEnvironmentVariable("DOCUMIND_FLOW_FRONTEND_ORIGIN") ?? address.ToString()) : address;
        var (process, _) = LaunchApi(connectionString, "Development", publicAddress.ToString(), lifetime, address.ToString());
        using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (process.HasExited) throw new FlowCheckException("The test API must start successfully.");
            try
            {
                using var readiness = await client.GetAsync("api/v1/health/ready");
                if (readiness.IsSuccessStatusCode) return (process, publicAddress);
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(100);
        }

        throw new FlowCheckException("The test API did not become ready.");
    }

    /// <summary>Verifies a startup guard without printing the captured process output.</summary>
    private static async Task AssertStartupFailureAsync(string connectionString, string environment,
        string applicationUrl, string expectedMessage, string assertion)
    {
        var (process, output) = LaunchApi(connectionString, environment, applicationUrl, "1.00:00:00");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert(process.ExitCode != 0 && output.ToString().Contains(expectedMessage), assertion);
    }

    /// <summary>Configures each child independently; passwords are passed in environment variables only.</summary>
    private static (Process Process, System.Text.StringBuilder Output) LaunchApi(string connectionString,
        string environment, string applicationUrl, string lifetime, string? listenUrl = null)
    {
        var apiDirectory = Path.Combine(repositoryRoot, "backend", "src", "DocuMind.Api");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = apiDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add(Path.Combine(apiDirectory, "bin", "Debug", "net10.0", "DocuMind.Api.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{FindAvailablePort()}");
        if (Uri.TryCreate(applicationUrl, UriKind.Absolute, out var appUri)
            && appUri.IsLoopback && environment == "Development")
        {
            start.ArgumentList[^1] = applicationUrl;
        }

        if (listenUrl is not null) start.ArgumentList[^1] = listenUrl;
        start.Environment["ConnectionStrings__DocuMind"] = connectionString;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        start.Environment["Accounts__ApplicationUrl"] = applicationUrl;
        start.Environment["Accounts__ConfirmationTokenLifetime"] = lifetime;
        start.Environment["Accounts__ResendCooldown"] = "00:00:01";
        // Restricted verification environments cannot write to Windows EventLog or normal user key directories.
        start.Environment["Logging__EventLog__LogLevel__Default"] = "None";
        start.Environment["LOCALAPPDATA"] = temporaryDirectory;
        var output = new System.Text.StringBuilder();
        var outputLock = new object();
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) => { lock (outputLock) output.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { lock (outputLock) output.AppendLine(args.Data); };
        process.Start();
        ApiProcesses.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return (process, output);
    }

    /// <summary>Reads only links destined for a generated test account, never logs preview contents.</summary>
    private static List<string> ReadPreviewLinks(string email)
    {
        if (!Directory.Exists(previewDirectory)) return [];
        var links = new List<string>();
        foreach (var path in Directory.EnumerateFiles(previewDirectory, "*.json"))
        {
            using var preview = JsonDocument.Parse(File.ReadAllText(path));
            if (!string.Equals(preview.RootElement.GetProperty("To").GetString(), email, StringComparison.OrdinalIgnoreCase)) continue;
            var body = preview.RootElement.GetProperty("TextBody").GetString()!;
            links.Add(body.Split('\n').Single(line => line.StartsWith("http", StringComparison.Ordinal)));
        }

        return links;
    }

    /// <summary>Reserves a free loopback port briefly to avoid conflicting with a user's running API.</summary>
    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Locates the checkout from build output, so checks do not depend on the current directory.</summary>
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "DocuMind.slnx"))) return directory.FullName;
        }

        throw new FlowCheckException("Cannot locate the DocuMind checkout.");
    }

    /// <summary>Tracks generated addresses so cleanup cannot remove a developer's unrelated previews.</summary>
    private static string NewEmail()
    {
        var email = $"flow-{Guid.NewGuid():N}@example.test";
        TestEmails.Add(email);
        return email;
    }

    /// <summary>Prints only a safe check label and fails the executable when behavior differs.</summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new FlowCheckException(message);
        Console.WriteLine("PASS: " + message);
    }

    /// <summary>Distinguishes controlled assertion messages from potentially sensitive provider exceptions.</summary>
    private sealed class FlowCheckException(string message) : Exception(message);
}

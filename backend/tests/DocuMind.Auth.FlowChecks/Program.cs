using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
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
            var databaseOptions = new DbContextOptionsBuilder<DocuMindDbContext>().UseNpgsql(testConnection).Options;
            await using var database = new DocuMindDbContext(databaseOptions);
            await database.Database.MigrateAsync();

            // Passwords and test email addresses are generated in memory and never written to tracked files.
            var password = "Aa9!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var email = NewEmail();
            var api = await StartApiAsync(testConnection);
            using var client = new HttpClient { BaseAddress = api.Address };
            var registration = new { email, password, confirmPassword = password };
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/register")
            {
                Content = JsonContent.Create(registration)
            };
            request.Headers.Host = "untrusted.example";
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

            using var duplicate = await client.PostAsJsonAsync("api/v1/auth/register",
                new { email, password = password + "Changed", confirmPassword = password + "Changed" });
            Assert(duplicate.StatusCode == HttpStatusCode.Accepted
                && await duplicate.Content.ReadAsStringAsync() == acceptedBody,
                "Duplicate registration has the same status and body.");
            Assert(await database.Users.CountAsync(account => account.NormalizedEmail == user.NormalizedEmail) == 1
                && ReadPreviewLinks(email).Count == 1, "Duplicate registration creates no second account or email.");
            Assert(await database.Users.AsNoTracking().Where(account => account.Id == user.Id)
                .Select(account => account.PasswordHash).SingleAsync() == user.PasswordHash,
                "Duplicate registration never changes the existing password hash.");

            using var invalidEmail = await client.PostAsJsonAsync("api/v1/auth/register",
                new { email = "not-an-email", password, confirmPassword = password });
            Assert(invalidEmail.StatusCode == HttpStatusCode.BadRequest, "Invalid email input returns 400.");
            var weakPassword = new string('a', 16);
            using var weak = await client.PostAsJsonAsync("api/v1/auth/register",
                new { email = NewEmail(), password = weakPassword, confirmPassword = weakPassword });
            Assert(weak.StatusCode == HttpStatusCode.BadRequest, "Identity password strength rules return 400.");
            using var mismatch = await client.PostAsJsonAsync("api/v1/auth/register",
                new { email = NewEmail(), password, confirmPassword = password + "x" });
            Assert(mismatch.StatusCode == HttpStatusCode.BadRequest, "Password confirmation mismatch returns 400.");
            Assert(await database.Users.CountAsync() == 1, "Invalid registration requests persist no accounts.");
            using var rateLimited = await client.PostAsJsonAsync("API/V1/AUTH/REGISTER/", registration);
            Assert(rateLimited.StatusCode == HttpStatusCode.TooManyRequests
                && rateLimited.Headers.RetryAfter is not null, "Registration enforces its five-per-minute IP limit even with route casing changes.");

            using var unknownResend = await client.PostAsJsonAsync("api/v1/auth/resend-confirmation", new { email = NewEmail() });
            Assert(unknownResend.StatusCode == HttpStatusCode.Accepted
                && await unknownResend.Content.ReadAsStringAsync() == acceptedBody,
                "Unknown-email resend preserves the generic response.");
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            using var resend = await client.PostAsJsonAsync("api/v1/auth/resend-confirmation", new { email });
            Assert(resend.StatusCode == HttpStatusCode.Accepted
                && await resend.Content.ReadAsStringAsync() == acceptedBody && ReadPreviewLinks(email).Count == 2,
                "Resend writes another preview for an unconfirmed account.");
            using var immediateResend = await client.PostAsJsonAsync("api/v1/auth/resend-confirmation", new { email });
            Assert(immediateResend.StatusCode == HttpStatusCode.Accepted && ReadPreviewLinks(email).Count == 2,
                "Recipient cooldown prevents immediate repeated delivery.");

            using var malformed = await client.GetAsync($"api/v1/auth/confirm-email?userId={user.Id}&token=***");
            Assert(malformed.StatusCode == HttpStatusCode.BadRequest
                && (await malformed.Content.ReadAsStringAsync()).Contains("Invalid or expired"),
                "Malformed confirmation tokens return a clear 400.");
            var forgedToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
            using var forged = await client.GetAsync($"api/v1/auth/confirm-email?userId={user.Id}&token={forgedToken}");
            Assert(forged.StatusCode == HttpStatusCode.BadRequest, "Well-encoded forged tokens cannot confirm an account.");
            Assert(!await database.Users.AsNoTracking().Where(account => account.Id == user.Id)
                .Select(account => account.EmailConfirmed).SingleAsync(), "Invalid tokens leave the account unconfirmed.");
            using var confirmed = await client.GetAsync(originalLink);
            Assert(confirmed.StatusCode == HttpStatusCode.OK
                && confirmed.Headers.CacheControl?.NoStore == true,
                "A valid token confirms the email and disables response caching.");
            Assert(await database.Users.AsNoTracking().Where(account => account.Id == user.Id)
                .Select(account => account.EmailConfirmed).SingleAsync(), "Confirmation persists in PostgreSQL.");
            using var confirmedResend = await client.PostAsJsonAsync("api/v1/auth/resend-confirmation", new { email });
            Assert(confirmedResend.StatusCode == HttpStatusCode.Accepted
                && await confirmedResend.Content.ReadAsStringAsync() == acceptedBody && ReadPreviewLinks(email).Count == 2,
                "Confirmed-account resend stays generic and sends no email.");

            using var fifthResend = await client.PostAsJsonAsync("api/v1/auth/resend-confirmation", new { email = NewEmail() });
            Assert(fifthResend.StatusCode == HttpStatusCode.Accepted, "Resend accepts requests within its IP limit.");
            using var limitedResend = await client.PostAsJsonAsync("api/v1/auth/resend-confirmation", new { email });
            Assert(limitedResend.StatusCode == HttpStatusCode.TooManyRequests, "Resend also enforces its per-IP rate limit.");

            // A second instance gives expiry an actual short lifetime, without waiting 24 hours.
            var expiringApi = await StartApiAsync(testConnection, lifetime: "00:00:01");
            using var expiringClient = new HttpClient { BaseAddress = expiringApi.Address };
            var expiringEmail = NewEmail();
            using var expiringRegistration = await expiringClient.PostAsJsonAsync("api/v1/auth/register",
                new { email = expiringEmail, password, confirmPassword = password });
            Assert(expiringRegistration.StatusCode == HttpStatusCode.Accepted, "Expiry test account is created.");
            var expiringLink = ReadPreviewLinks(expiringEmail).Single();
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            using var expired = await expiringClient.GetAsync(expiringLink);
            Assert(expired.StatusCode == HttpStatusCode.BadRequest
                && (await expired.Content.ReadAsStringAsync()).Contains("Invalid or expired"),
                "Identity rejects an expired confirmation token with a clear 400.");
            Assert(!await database.Users.AsNoTracking().Where(account => account.Email == expiringEmail)
                .Select(account => account.EmailConfirmed).SingleAsync(), "Expired tokens leave the account unconfirmed.");

            // Concurrent requests exercise the database uniqueness race rather than just a precheck.
            var raceEmail = NewEmail();
            var raceResponses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                expiringClient.PostAsJsonAsync("api/v1/auth/register",
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

            using var caseDuplicate = await expiringClient.PostAsJsonAsync("api/v1/auth/register",
                new { email = email.ToUpperInvariant(), password, confirmPassword = password });
            Assert(caseDuplicate.StatusCode == HttpStatusCode.Accepted
                && await caseDuplicate.Content.ReadAsStringAsync() == acceptedBody
                && await database.Users.CountAsync(account => account.NormalizedEmail == user.NormalizedEmail) == 1,
                "Email uniqueness and duplicate privacy are case-insensitive.");

            await AssertStartupFailureAsync(testConnection, "Production", "https://documind.example",
                "No production email sender is configured", "Production refuses to start without a real email sender.");
            await AssertStartupFailureAsync(testConnection, "Development", "not-an-absolute-url",
                "Accounts:ApplicationUrl", "Invalid application URLs fail startup validation.");
            Console.WriteLine("All registration and email-confirmation flow checks passed.");
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

    /// <summary>Starts a real API process with test-only configuration and waits for readiness.</summary>
    private static async Task<(Process Process, Uri Address)> StartApiAsync(string connectionString,
        string lifetime = "1.00:00:00")
    {
        var address = new Uri($"http://127.0.0.1:{FindAvailablePort()}/");
        var (process, _) = LaunchApi(connectionString, "Development", address.ToString(), lifetime);
        using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (process.HasExited) throw new FlowCheckException("The test API must start successfully.");
            try
            {
                using var readiness = await client.GetAsync("api/v1/health/ready");
                if (readiness.IsSuccessStatusCode) return (process, address);
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
        string environment, string applicationUrl, string lifetime)
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

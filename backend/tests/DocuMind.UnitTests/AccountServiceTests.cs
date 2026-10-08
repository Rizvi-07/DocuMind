using System.Text;
using DocuMind.Data.Entities;
using DocuMind.Services.Accounts;
using DocuMind.Services.Email;
using DocuMind.UnitTests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using Xunit;

namespace DocuMind.UnitTests;

/// <summary>Tests account privacy, validation ordering, token encoding, and delivery decisions at the service boundary.</summary>
public sealed class AccountServiceTests
{
    /// <summary>Password errors are deduplicated and returned before an email existence lookup can reveal an account.</summary>
    [Fact]
    public async Task Register_InvalidPassword_StopsBeforeLookingUpEmail()
    {
        using var fixture = new AccountFixture();
        var validator = new Mock<IPasswordValidator<ApplicationUser>>();
        var error = new IdentityError { Code = "PasswordTooShort", Description = "Use a longer password." };
        validator.Setup(v => v.ValidateAsync(fixture.Identity.Users.Object, It.IsAny<ApplicationUser>(), fixture.Password))
            .ReturnsAsync(IdentityResult.Failed(error, error));
        fixture.Identity.Users.Object.PasswordValidators.Add(validator.Object);

        var result = await fixture.Service.RegisterAsync("account@example.test", fixture.Password, fixture.Token);

        Assert.Equal(new[] { error.Description }, result.Errors);
        fixture.Identity.Users.Verify(users => users.FindByEmailAsync(It.IsAny<string>()), Times.Never);
        fixture.Sender.VerifyNoOtherCalls();
    }

    /// <summary>Non-uniqueness user rules still apply before the duplicate-account shortcut.</summary>
    [Fact]
    public async Task Register_InvalidUser_ReturnsOnlyNonDuplicateValidationErrors()
    {
        using var fixture = new AccountFixture();
        var validator = new Mock<IUserValidator<ApplicationUser>>();
        var invalid = new IdentityError { Code = "InvalidUserName", Description = "Unsupported account name." };
        validator.Setup(v => v.ValidateAsync(fixture.Identity.Users.Object, It.IsAny<ApplicationUser>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "DuplicateEmail", Description = "private" }, invalid, invalid));
        fixture.Identity.Users.Object.UserValidators.Add(validator.Object);

        var result = await fixture.Service.RegisterAsync("account@example.test", fixture.Password, fixture.Token);

        Assert.Equal(new[] { invalid.Description }, result.Errors);
        fixture.Identity.Users.Verify(users => users.FindByEmailAsync(It.IsAny<string>()), Times.Never);
        fixture.Sender.VerifyNoOtherCalls();
    }

    /// <summary>Existing accounts retain their credentials, receive no email, and expose no duplicate error.</summary>
    [Fact]
    public async Task Register_Duplicate_UsesGenericResultAndDoesHashingWork()
    {
        using var fixture = new AccountFixture();
        var existing = new ApplicationUser { Email = "account@example.test", PasswordHash = "original-test-hash" };
        var validator = new Mock<IUserValidator<ApplicationUser>>();
        validator.Setup(v => v.ValidateAsync(fixture.Identity.Users.Object, It.IsAny<ApplicationUser>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = "private" }));
        fixture.Identity.Users.Object.UserValidators.Add(validator.Object);
        fixture.Identity.Users.Setup(users => users.FindByEmailAsync(existing.Email)).ReturnsAsync(existing);
        fixture.Identity.Hasher.Setup(hasher => hasher.HashPassword(It.IsAny<ApplicationUser>(), fixture.Password))
            .Returns("unused-test-hash");

        var result = await fixture.Service.RegisterAsync(" account@example.test ", fixture.Password, fixture.Token);

        Assert.Empty(result.Errors);
        Assert.Equal("original-test-hash", existing.PasswordHash);
        fixture.Identity.Users.Verify(users => users.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
        fixture.Identity.Hasher.Verify(hasher => hasher.HashPassword(It.IsAny<ApplicationUser>(), fixture.Password), Times.Once);
        fixture.Sender.VerifyNoOtherCalls();
    }

    /// <summary>New accounts start unconfirmed and links round-trip special token characters through the configured URL.</summary>
    [Fact]
    public async Task Register_NewAccount_CreatesUnconfirmedUserAndEncodedTrustedLink()
    {
        using var fixture = new AccountFixture();
        ApplicationUser? created = null;
        fixture.Identity.Users.Setup(users => users.FindByEmailAsync("account@example.test")).ReturnsAsync((ApplicationUser?)null);
        fixture.Identity.Users.Setup(users => users.CreateAsync(It.IsAny<ApplicationUser>(), fixture.Password))
            .Callback<ApplicationUser, string>((user, _) => created = user).ReturnsAsync(IdentityResult.Success);
        var rawToken = "synthetic +/== token with Unicode: é";
        fixture.Identity.Users.Setup(users => users.GenerateEmailConfirmationTokenAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(rawToken);
        string? body = null;
        fixture.Sender.Setup(sender => sender.SendAsync("account@example.test", It.IsAny<string>(), It.IsAny<string>(), fixture.Token))
            .Callback<string, string, string, CancellationToken>((_, _, text, _) => body = text).Returns(Task.CompletedTask);

        var result = await fixture.Service.RegisterAsync(" account@example.test ", fixture.Password, fixture.Token);

        Assert.Empty(result.Errors);
        Assert.NotNull(created);
        Assert.False(created.EmailConfirmed);
        Assert.Equal("account@example.test", created.Email);
        Assert.Equal(created.Email, created.UserName);
        Assert.NotNull(body);
        var link = new Uri(body.Split('\n').Single(line => line.StartsWith("https://", StringComparison.Ordinal)));
        var query = QueryHelpers.ParseQuery(link.Query);
        Assert.Equal("trusted.example.test", link.Host);
        Assert.Equal("/documind/api/v1/auth/confirm-email", link.AbsolutePath);
        Assert.Equal(created.Id.ToString(), query["userId"].ToString());
        var encoded = query["token"].ToString();
        Assert.All(encoded, character => Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        Assert.Equal(rawToken, Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encoded)));
    }

    /// <summary>Identity creation errors hide uniqueness but retain actionable password/user-policy feedback.</summary>
    [Theory]
    [InlineData("DuplicateEmail", true)]
    [InlineData("DuplicateUserName", true)]
    [InlineData("PasswordTooShort", false)]
    public async Task Register_IdentityFailure_FiltersDuplicateErrors(string code, bool hidden)
    {
        using var fixture = new AccountFixture();
        fixture.Identity.Users.Setup(users => users.FindByEmailAsync("account@example.test")).ReturnsAsync((ApplicationUser?)null);
        fixture.Identity.Users.Setup(users => users.CreateAsync(It.IsAny<ApplicationUser>(), fixture.Password))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = code, Description = "validation message" }));

        var result = await fixture.Service.RegisterAsync("account@example.test", fixture.Password, fixture.Token);

        Assert.Equal(hidden ? Array.Empty<string>() : new[] { "validation message" }, result.Errors);
        fixture.Sender.VerifyNoOtherCalls();
    }

    /// <summary>Only the known username uniqueness race is suppressed; other storage failures remain visible to the caller.</summary>
    [Theory]
    [InlineData("23505", "UserNameIndex", true)]
    [InlineData("23505", "DifferentIndex", false)]
    [InlineData("08006", "UserNameIndex", false)]
    public async Task Register_StorageFailure_SuppressesOnlyExpectedDuplicateRace(string sqlState, string constraint, bool duplicate)
    {
        using var fixture = new AccountFixture();
        var exception = new DbUpdateException("synthetic storage failure",
            new PostgresException("synthetic provider failure", "ERROR", "ERROR", sqlState, constraintName: constraint));
        fixture.Identity.Users.Setup(users => users.FindByEmailAsync("account@example.test")).ReturnsAsync((ApplicationUser?)null);
        fixture.Identity.Users.Setup(users => users.CreateAsync(It.IsAny<ApplicationUser>(), fixture.Password)).ThrowsAsync(exception);

        if (duplicate)
            Assert.Empty((await fixture.Service.RegisterAsync("account@example.test", fixture.Password, fixture.Token)).Errors);
        else
            Assert.Same(exception, await Assert.ThrowsAsync<DbUpdateException>(
                () => fixture.Service.RegisterAsync("account@example.test", fixture.Password, fixture.Token)));
        fixture.Sender.VerifyNoOtherCalls();
    }

    /// <summary>Malformed links stop before looking up account state or invoking Identity's token validator.</summary>
    [Theory]
    [InlineData("not-a-guid", "abc")]
    [InlineData("00000000-0000-0000-0000-000000000001", "***")]
    public async Task Confirm_MalformedLink_DoesNotLookUpUser(string id, string encodedToken)
    {
        using var fixture = new AccountFixture();
        Assert.False(await fixture.Service.ConfirmEmailAsync(id, encodedToken, fixture.Token));
        fixture.Identity.Users.VerifyNoOtherCalls();
    }

    /// <summary>Oversized token input is rejected at the service boundary even without MVC validation.</summary>
    [Fact]
    public async Task Confirm_OversizedToken_DoesNotLookUpUser()
    {
        using var fixture = new AccountFixture();
        Assert.False(await fixture.Service.ConfirmEmailAsync(Guid.NewGuid().ToString(), new string('a', 4097), fixture.Token));
        fixture.Identity.Users.VerifyNoOtherCalls();
    }

    /// <summary>A validly encoded token cannot confirm an account that no longer exists.</summary>
    [Fact]
    public async Task Confirm_MissingUser_ReturnsFalse()
    {
        using var fixture = new AccountFixture();
        var id = Guid.NewGuid().ToString();
        fixture.Identity.Users.Setup(users => users.FindByIdAsync(id)).ReturnsAsync((ApplicationUser?)null);
        Assert.False(await fixture.Service.ConfirmEmailAsync(id, WebEncoders.Base64UrlEncode("token"u8.ToArray()), fixture.Token));
    }

    /// <summary>Confirmation always delegates the decoded token to Identity, including for already-confirmed users.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Confirm_DecodesTokenAndRequiresIdentitySuccess(bool alreadyConfirmed, bool valid)
    {
        using var fixture = new AccountFixture();
        var user = new ApplicationUser { EmailConfirmed = alreadyConfirmed };
        var rawToken = "synthetic +/== é";
        fixture.Identity.Users.Setup(users => users.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        fixture.Identity.Users.Setup(users => users.ConfirmEmailAsync(user, rawToken))
            .ReturnsAsync(valid ? IdentityResult.Success : IdentityResult.Failed(new IdentityError()));

        Assert.Equal(valid, await fixture.Service.ConfirmEmailAsync(user.Id.ToString(),
            WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(rawToken)), fixture.Token));
        fixture.Identity.Users.Verify(users => users.ConfirmEmailAsync(user, rawToken), Times.Once);
    }

    /// <summary>Resend ignores absent/confirmed accounts and delivers only for eligible unconfirmed users.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Resend_DeliversOnlyForEligibleAccounts(bool exists, bool confirmed)
    {
        using var fixture = new AccountFixture();
        var user = new ApplicationUser { Email = "account@example.test", EmailConfirmed = confirmed };
        fixture.Identity.Users.Setup(users => users.FindByEmailAsync(user.Email!)).ReturnsAsync(exists ? user : null);
        if (exists && !confirmed) fixture.AllowDelivery(user);

        await fixture.Service.ResendConfirmationAsync(" account@example.test ", fixture.Token);

        fixture.Sender.Verify(sender => sender.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), fixture.Token),
            exists && !confirmed ? Times.Once() : Times.Never());
    }

    /// <summary>The cache is shared across scoped services and limits each user independently without sleeping.</summary>
    [Fact]
    public async Task Resend_CooldownIsSharedAcrossServiceInstancesButNotAccounts()
    {
        using var fixture = new AccountFixture();
        var first = new ApplicationUser { Email = "first@example.test" };
        var second = new ApplicationUser { Email = "second@example.test" };
        foreach (var user in new[] { first, second })
        {
            fixture.Identity.Users.Setup(users => users.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
            fixture.AllowDelivery(user);
        }
        await fixture.Service.ResendConfirmationAsync(first.Email!, fixture.Token);
        await fixture.CreateService().ResendConfirmationAsync(first.Email!, fixture.Token);
        await fixture.CreateService().ResendConfirmationAsync(second.Email!, fixture.Token);

        fixture.Sender.Verify(sender => sender.SendAsync(first.Email!, It.IsAny<string>(), It.IsAny<string>(), fixture.Token), Times.Once);
        fixture.Sender.Verify(sender => sender.SendAsync(second.Email!, It.IsAny<string>(), It.IsAny<string>(), fixture.Token), Times.Once);
    }

    /// <summary>Best-effort delivery hides provider failures, removes cooldown for retry, and logs no recipient/token/exception text.</summary>
    [Fact]
    public async Task Resend_DeliveryFailure_AllowsRetryAndKeepsLogsPrivate()
    {
        using var fixture = new AccountFixture();
        var user = new ApplicationUser { Email = "private-recipient@example.test" };
        var rawToken = "private-synthetic-token";
        var providerMessage = "private-synthetic-provider-message";
        fixture.Identity.Users.Setup(users => users.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        fixture.Identity.Users.Setup(users => users.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync(rawToken);
        fixture.Sender.SetupSequence(sender => sender.SendAsync(user.Email!, It.IsAny<string>(), It.IsAny<string>(), fixture.Token))
            .ThrowsAsync(new InvalidOperationException(providerMessage)).Returns(Task.CompletedTask);

        await fixture.Service.ResendConfirmationAsync(user.Email!, fixture.Token);
        await fixture.Service.ResendConfirmationAsync(user.Email!, fixture.Token);

        fixture.Sender.Verify(sender => sender.SendAsync(user.Email!, It.IsAny<string>(), It.IsAny<string>(), fixture.Token), Times.Exactly(2));
        var entry = Assert.Single(fixture.Logger.Entries);
        Assert.Null(entry.Exception);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        foreach (var secret in new[] { user.Email!, rawToken, providerMessage }) Assert.DoesNotContain(secret, entry.Message);
    }

    /// <summary>A canceled delivery is propagated rather than mislabeled as a successful best-effort provider failure.</summary>
    [Fact]
    public async Task Resend_CanceledDelivery_PropagatesCancellation()
    {
        using var fixture = new AccountFixture();
        var user = new ApplicationUser { Email = "account@example.test" };
        fixture.Identity.Users.Setup(users => users.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        fixture.Identity.Users.Setup(users => users.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("synthetic-token");
        fixture.Sender.Setup(sender => sender.SendAsync(user.Email!, It.IsAny<string>(), It.IsAny<string>(), fixture.Token))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ResendConfirmationAsync(user.Email!, fixture.Token));
        Assert.Empty(fixture.Logger.Entries);
    }

    /// <summary>Canceled calls do not touch Identity, deliver email, or validate secret material.</summary>
    [Theory]
    [InlineData("register")]
    [InlineData("confirm")]
    [InlineData("resend")]
    public async Task CanceledOperation_DoesNotInvokeDependencies(string operation)
    {
        using var fixture = new AccountFixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "register": await fixture.Service.RegisterAsync("account@example.test", fixture.Password, canceled.Token); break;
                case "confirm": await fixture.Service.ConfirmEmailAsync(Guid.NewGuid().ToString(), "unused", canceled.Token); break;
                default: await fixture.Service.ResendConfirmationAsync("account@example.test", canceled.Token); break;
            }
        });
        fixture.Identity.Users.VerifyNoOtherCalls();
        fixture.Sender.VerifyNoOtherCalls();
    }

    /// <summary>Owns a fresh memory cache per test so parallel cases cannot share cooldown or account state.</summary>
    private sealed class AccountFixture : IDisposable
    {
        public IdentityDoubles Identity { get; } = new();
        public Mock<IEmailSender> Sender { get; } = new(MockBehavior.Strict);
        public RecordingLogger<AccountService> Logger { get; } = new();
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());
        public string Password { get; } = Guid.NewGuid().ToString("N");
        public CancellationToken Token => TestContext.Current.CancellationToken;
        public AccountService Service => CreateService();

        /// <summary>Services share the fixture cache, matching the application's singleton-cache/scoped-service arrangement.</summary>
        public AccountService CreateService() => new(Identity.Users.Object, Sender.Object,
            Options.Create(new AccountOptions { ApplicationUrl = "https://trusted.example.test/documind/",
                ResendCooldown = TimeSpan.FromMinutes(1) }), Cache, Logger);

        /// <summary>Configures one eligible account's token and delivery without generating a real email artifact.</summary>
        public void AllowDelivery(ApplicationUser user)
        {
            Identity.Users.Setup(users => users.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("synthetic-token");
            Sender.Setup(sender => sender.SendAsync(user.Email!, It.IsAny<string>(), It.IsAny<string>(), Token)).Returns(Task.CompletedTask);
        }

        /// <summary>Releases the cache after each test to avoid leaking cooldown resources.</summary>
        public void Dispose() => Cache.Dispose();
    }
}

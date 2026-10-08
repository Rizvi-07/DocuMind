using System.Security.Claims;
using DocuMind.Data.Entities;
using DocuMind.Services.Accounts;
using DocuMind.UnitTests.Support;
using Microsoft.AspNetCore.Identity;
using Moq;
using Xunit;

namespace DocuMind.UnitTests;

/// <summary>Protects custom session delegation and safe account projection without reimplementing Identity.</summary>
public sealed class IdentitySessionServiceTests
{
    /// <summary>Unknown accounts get hashing work and a generic failure without invoking password sign-in.</summary>
    [Fact]
    public async Task Login_UnknownEmail_PerformsHashingAndDoesNotSignIn()
    {
        var identity = new IdentityDoubles();
        var password = Guid.NewGuid().ToString("N");
        identity.Users.Setup(users => users.FindByEmailAsync("missing@example.test")).ReturnsAsync((ApplicationUser?)null);
        identity.Hasher.Setup(hasher => hasher.HashPassword(It.IsAny<ApplicationUser>(), password)).Returns("unused-test-hash");
        var service = new IdentitySessionService(identity.Users.Object, identity.SignIn.Object);

        Assert.False(await service.LoginAsync(" missing@example.test ", password, false, TestContext.Current.CancellationToken));
        identity.Hasher.Verify(hasher => hasher.HashPassword(It.IsAny<ApplicationUser>(), password), Times.Once);
        identity.SignIn.VerifyNoOtherCalls();
    }

    /// <summary>Every unsuccessful Identity outcome remains false, and failed-password lockout is always enabled.</summary>
    [Theory]
    [InlineData("success", false)]
    [InlineData("success", true)]
    [InlineData("failed", false)]
    [InlineData("not-allowed", false)]
    [InlineData("locked-out", false)]
    [InlineData("two-factor", false)]
    public async Task Login_DelegatesRememberMeAndLockout_AndMapsIdentityResult(string outcome, bool rememberMe)
    {
        var identity = new IdentityDoubles();
        var user = new ApplicationUser { Email = "account@example.test" };
        var password = Guid.NewGuid().ToString("N");
        var token = TestContext.Current.CancellationToken;
        var result = outcome switch
        {
            "success" => SignInResult.Success, "not-allowed" => SignInResult.NotAllowed,
            "locked-out" => SignInResult.LockedOut, "two-factor" => SignInResult.TwoFactorRequired,
            _ => SignInResult.Failed
        };
        identity.Users.Setup(users => users.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        identity.SignIn.Setup(signIn => signIn.PasswordSignInAsync(user, password, rememberMe, true)).ReturnsAsync(result);
        var service = new IdentitySessionService(identity.Users.Object, identity.SignIn.Object);

        Assert.Equal(outcome == "success", await service.LoginAsync(" " + user.Email + " ", password, rememberMe, token));
        identity.SignIn.Verify(signIn => signIn.PasswordSignInAsync(user, password, rememberMe, true), Times.Once);
    }

    /// <summary>Logout delegates to Identity rather than attempting to clear a handwritten cookie.</summary>
    [Fact]
    public async Task Logout_DelegatesToIdentitySignOut()
    {
        var identity = new IdentityDoubles();
        identity.SignIn.Setup(signIn => signIn.SignOutAsync()).Returns(Task.CompletedTask);
        var service = new IdentitySessionService(identity.Users.Object, identity.SignIn.Object);

        await service.LogoutAsync(TestContext.Current.CancellationToken);
        identity.SignIn.Verify(signIn => signIn.SignOutAsync(), Times.Once);
    }

    /// <summary>A missing Identity account cannot be returned as a current user.</summary>
    [Fact]
    public async Task GetCurrentAccount_MissingUser_ReturnsNull()
    {
        var identity = new IdentityDoubles();
        var principal = new ClaimsPrincipal();
        identity.Users.Setup(users => users.GetUserAsync(principal)).ReturnsAsync((ApplicationUser?)null);
        var service = new IdentitySessionService(identity.Users.Object, identity.SignIn.Object);

        Assert.Null(await service.GetCurrentAccountAsync(principal, TestContext.Current.CancellationToken));
    }

    /// <summary>The service projects account data into its safe DTO, preserving values without leaking Identity secrets.</summary>
    [Fact]
    public async Task GetCurrentAccount_ReturnsOnlySafeFields()
    {
        var identity = new IdentityDoubles();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([], "unit-test"));
        var user = new ApplicationUser { Email = "account@example.test", EmailConfirmed = true,
            CreatedAt = DateTimeOffset.Parse("2026-01-01T12:00:00Z"), PasswordHash = "private-test-hash",
            SecurityStamp = "private-test-stamp", AccessFailedCount = 4 };
        identity.Users.Setup(users => users.GetUserAsync(principal)).ReturnsAsync(user);
        var service = new IdentitySessionService(identity.Users.Object, identity.SignIn.Object);

        var account = await service.GetCurrentAccountAsync(principal, TestContext.Current.CancellationToken);
        Assert.Equal(new CurrentAccount(user.Id, user.Email, true, user.CreatedAt), account);
        // Checking the DTO shape prevents later additions from silently exposing Identity internals.
        Assert.Equal(new[] { "CreatedAt", "Email", "EmailConfirmed", "Id" },
            typeof(CurrentAccount).GetProperties().Select(property => property.Name).Order().ToArray());
    }

    /// <summary>Pre-canceled operations must not inspect credentials, create cookies, or access account storage.</summary>
    [Theory]
    [InlineData("login")]
    [InlineData("logout")]
    [InlineData("current-user")]
    public async Task CanceledOperation_DoesNotInvokeIdentity(string operation)
    {
        var identity = new IdentityDoubles();
        var service = new IdentitySessionService(identity.Users.Object, identity.SignIn.Object);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "login": await service.LoginAsync("account@example.test", "unused", false, canceled.Token); break;
                case "logout": await service.LogoutAsync(canceled.Token); break;
                default: await service.GetCurrentAccountAsync(new ClaimsPrincipal(), canceled.Token); break;
            }
        });
        identity.Users.VerifyNoOtherCalls();
        identity.SignIn.VerifyNoOtherCalls();
    }
}

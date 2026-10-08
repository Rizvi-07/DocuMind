using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocuMind.Api.Contracts;
using DocuMind.Api.Controllers;
using DocuMind.Services.Accounts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocuMind.UnitTests;

/// <summary>Checks HTTP decisions and delegation; middleware authorization, validation, and CSRF remain integration concerns.</summary>
public sealed class AuthControllerTests
{
    /// <summary>Login preserves credentials, persistence choice, and cancellation while mapping service outcomes to 204/401.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Login_DelegatesInputAndReturnsExpectedResponse(bool success, bool rememberMe)
    {
        using var fixture = new ControllerFixture();
        var request = new LoginRequest { Email = "account@example.test", Password = Guid.NewGuid().ToString("N"), RememberMe = rememberMe };
        fixture.Sessions.Setup(sessions => sessions.LoginAsync(request.Email, request.Password, rememberMe, fixture.Token)).ReturnsAsync(success);

        var response = await fixture.Controller.Login(request, fixture.Token);

        if (success) Assert.IsType<NoContentResult>(response);
        else
        {
            var failure = Assert.IsType<UnauthorizedObjectResult>(response);
            Assert.Equal(401, failure.StatusCode);
            Assert.Equal("Unable to sign in. Check your credentials and try again.",
                JsonSerializer.SerializeToElement(failure.Value).GetProperty("message").GetString());
        }
        fixture.Sessions.Verify(sessions => sessions.LoginAsync(request.Email, request.Password, rememberMe, fixture.Token), Times.Once);
    }

    /// <summary>Logout must invoke session clearing exactly once before returning an empty success response.</summary>
    [Fact]
    public async Task Logout_ClearsSessionBeforeReturningNoContent()
    {
        using var fixture = new ControllerFixture();
        fixture.Sessions.Setup(sessions => sessions.LogoutAsync(fixture.Token)).Returns(Task.CompletedTask);

        Assert.IsType<NoContentResult>(await fixture.Controller.Logout(fixture.Token));
        fixture.Sessions.Verify(sessions => sessions.LogoutAsync(fixture.Token), Times.Once);
    }

    /// <summary>The current authenticated principal is passed to the service and only its safe projection is returned.</summary>
    [Fact]
    public async Task Me_ReturnsSafeAccountForCurrentPrincipal()
    {
        using var fixture = new ControllerFixture();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([], "unit-test"));
        fixture.Context.User = principal;
        var account = new CurrentAccount(Guid.NewGuid(), "account@example.test", true, DateTimeOffset.UtcNow);
        fixture.Sessions.Setup(sessions => sessions.GetCurrentAccountAsync(principal, fixture.Token)).ReturnsAsync(account);

        var response = Assert.IsType<OkObjectResult>(await fixture.Controller.Me(fixture.Token));

        Assert.Same(account, response.Value);
        Assert.Equal(new[] { "createdAt", "email", "emailConfirmed", "id" },
            JsonSerializer.SerializeToElement(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                .EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    /// <summary>A stale principal whose account no longer exists receives 401 rather than a null success object.</summary>
    [Fact]
    public async Task Me_MissingAccount_ReturnsUnauthorized()
    {
        using var fixture = new ControllerFixture();
        fixture.Sessions.Setup(sessions => sessions.GetCurrentAccountAsync(fixture.Context.User, fixture.Token)).ReturnsAsync((CurrentAccount?)null);
        Assert.IsType<UnauthorizedResult>(await fixture.Controller.Me(fixture.Token));
    }

    /// <summary>Registration delegates the original input and never returns private account state.</summary>
    [Fact]
    public async Task Register_Accepted_ReturnsGenericMessageWithoutSigningIn()
    {
        using var fixture = new ControllerFixture();
        var request = new RegisterRequest { Email = "account@example.test", Password = Guid.NewGuid().ToString("N") };
        fixture.Accounts.Setup(accounts => accounts.RegisterAsync(request.Email, request.Password, fixture.Token))
            .ReturnsAsync(new RegistrationResult([]));

        var response = Assert.IsType<AcceptedResult>(await fixture.Controller.Register(request, fixture.Token));

        Assert.Equal(202, response.StatusCode);
        Assert.Equal("If this address is eligible, a confirmation email will be sent. Check your inbox.",
            JsonSerializer.SerializeToElement(response.Value).GetProperty("message").GetString());
        fixture.Sessions.VerifyNoOtherCalls();
    }

    /// <summary>Safe policy feedback is exposed as validation errors, not as account existence information.</summary>
    [Fact]
    public async Task Register_PolicyErrors_ReturnsValidationProblem()
    {
        using var fixture = new ControllerFixture();
        var request = new RegisterRequest { Email = "account@example.test", Password = "attempt" };
        fixture.Accounts.Setup(accounts => accounts.RegisterAsync(request.Email, request.Password, fixture.Token))
            .ReturnsAsync(new RegistrationResult(["Use a stronger password."]));

        var response = Assert.IsAssignableFrom<ObjectResult>(await fixture.Controller.Register(request, fixture.Token));
        var details = Assert.IsType<ValidationProblemDetails>(response.Value);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(new[] { "Use a stronger password." }, details.Errors["Registration"]);
    }

    /// <summary>Resend and accepted registration share the same generic response regardless of hidden delivery decisions.</summary>
    [Fact]
    public async Task Resend_DelegatesEmailAndUsesSameResponseAsRegistration()
    {
        using var fixture = new ControllerFixture();
        var email = "account@example.test";
        fixture.Accounts.Setup(accounts => accounts.ResendConfirmationAsync(email, fixture.Token)).Returns(Task.CompletedTask);
        fixture.Accounts.Setup(accounts => accounts.RegisterAsync(email, "unused", fixture.Token)).ReturnsAsync(new RegistrationResult([]));

        var resend = Assert.IsType<AcceptedResult>(await fixture.Controller.ResendConfirmation(new() { Email = email }, fixture.Token));
        var register = Assert.IsType<AcceptedResult>(await fixture.Controller.Register(new() { Email = email, Password = "unused" }, fixture.Token));

        Assert.Equal(register.StatusCode, resend.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(register.Value), JsonSerializer.Serialize(resend.Value));
        fixture.Accounts.Verify(accounts => accounts.ResendConfirmationAsync(email, fixture.Token), Times.Once);
    }

    /// <summary>JSON and form confirmation map invalid tokens to the same clear problem response.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Confirm_JsonAndForm_ReturnSameTokenResult(bool success, bool form)
    {
        using var fixture = new ControllerFixture();
        var request = new ConfirmEmailRequest { UserId = Guid.NewGuid().ToString(), Token = "synthetic-token" };
        fixture.Accounts.Setup(accounts => accounts.ConfirmEmailAsync(request.UserId, request.Token, fixture.Token)).ReturnsAsync(success);

        var response = form ? await fixture.Controller.ConfirmEmailForm(request, fixture.Token)
            : await fixture.Controller.ConfirmEmail(request, fixture.Token);

        if (success) Assert.IsType<OkObjectResult>(response);
        else
        {
            var failure = Assert.IsType<BadRequestObjectResult>(response);
            var problem = Assert.IsType<ProblemDetails>(failure.Value);
            Assert.Equal("Invalid or expired confirmation link.", problem.Title);
            Assert.Equal(400, problem.Status);
        }
        Assert.Equal("no-referrer", fixture.Context.Response.Headers["Referrer-Policy"].ToString());
    }

    /// <summary>The bootstrap endpoint returns only the request token supplied by antiforgery, never the cookie token.</summary>
    [Fact]
    public void Csrf_ReturnsRequestTokenFromAntiforgery()
    {
        using var fixture = new ControllerFixture();
        fixture.Antiforgery.Setup(antiforgery => antiforgery.GetAndStoreTokens(fixture.Context))
            .Returns(new AntiforgeryTokenSet("synthetic-request-token", "private-cookie-token", "__RequestVerificationToken", "X-CSRF-TOKEN"));

        var response = Assert.IsType<OkObjectResult>(fixture.Controller.Csrf());
        var json = JsonSerializer.SerializeToElement(response.Value);

        Assert.Equal("synthetic-request-token", json.GetProperty("requestToken").GetString());
        Assert.Single(json.EnumerateObject());
    }

    /// <summary>Email-link GET is safe, posts to the application path, and HTML-encodes attacker-controlled attributes.</summary>
    [Fact]
    public void ConfirmationPage_EncodesInputAndNeverMutatesAccount()
    {
        using var fixture = new ControllerFixture();
        fixture.Context.Request.PathBase = "/documind";
        var hostile = "\"><script>alert('test')</script>";
        fixture.Antiforgery.Setup(antiforgery => antiforgery.GetAndStoreTokens(fixture.Context))
            .Returns(new AntiforgeryTokenSet("synthetic-form-token", "private-cookie-token", "__RequestVerificationToken", "X-CSRF-TOKEN"));

        var response = Assert.IsType<ContentResult>(fixture.Controller.ConfirmationPage(new() { UserId = hostile, Token = hostile }));

        Assert.Equal("text/html; charset=utf-8", response.ContentType);
        Assert.NotNull(response.Content);
        Assert.Contains(HtmlEncoder.Default.Encode(hostile), response.Content);
        Assert.DoesNotContain(hostile, response.Content);
        Assert.DoesNotContain("private-cookie-token", response.Content);
        Assert.Contains("action=\"/documind/api/v1/auth/confirm-email\"", response.Content);
        Assert.Contains("name=\"__RequestVerificationToken\" value=\"synthetic-form-token\"", response.Content);
        Assert.Contains("form-action 'self'", fixture.Context.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Contains("frame-ancestors 'none'", fixture.Context.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Equal("no-referrer", fixture.Context.Response.Headers["Referrer-Policy"].ToString());
        fixture.Accounts.VerifyNoOtherCalls();
    }

    /// <summary>Provides a real MVC validation-problem factory and a private in-memory HTTP context for each case.</summary>
    private sealed class ControllerFixture : IDisposable
    {
        private readonly ServiceProvider provider;
        public Mock<IAccountService> Accounts { get; } = new(MockBehavior.Strict);
        public Mock<IIdentitySessionService> Sessions { get; } = new(MockBehavior.Strict);
        public Mock<IAntiforgery> Antiforgery { get; } = new(MockBehavior.Strict);
        public DefaultHttpContext Context { get; } = new();
        public CancellationToken Token => TestContext.Current.CancellationToken;
        public AuthController Controller { get; }

        /// <summary>Registers only the MVC services needed for HTTP results; no application host is started.</summary>
        public ControllerFixture()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllers();
            provider = services.BuildServiceProvider();
            Context.RequestServices = provider;
            Controller = new AuthController(Accounts.Object, Sessions.Object, Antiforgery.Object)
                { ControllerContext = new ControllerContext { HttpContext = Context } };
        }

        /// <summary>Releases the test-local service provider after assertions complete.</summary>
        public void Dispose() => provider.Dispose();
    }
}

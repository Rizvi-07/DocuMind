using System.Text.Encodings.Web;
using DocuMind.Api.Configuration;
using DocuMind.Api.Contracts;
using DocuMind.Services.Accounts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DocuMind.Api.Controllers;

/// <summary>Maps account use cases to HTTP responses; account and session logic remain in Services.</summary>
[ApiController, Authorize, Route("api/v1/auth"), RequestSizeLimit(16 * 1024)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(IAccountService accounts, IIdentitySessionService sessions, IAntiforgery antiforgery)
    : ControllerBase
{
    // Identical status and body are used for new, duplicate, and unknown eligible addresses.
    private const string AcceptedMessage =
        "If this address is eligible, a confirmation email will be sent. Check your inbox.";

    /// <summary>Returns a request token and its paired HttpOnly cookie for anonymous or signed-in callers.</summary>
    [HttpGet("csrf"), AllowAnonymous, EnableRateLimiting("account-bootstrap")]
    public IActionResult Csrf()
    {
        // Tokens are bound to the current identity. Fetch a new one after login or logout.
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { requestToken = tokens.RequestToken });
    }

    /// <summary>Accepts valid registration input without disclosing account existence or signing in.</summary>
    [HttpPost("register"), AllowAnonymous, Consumes("application/json"), EnableRateLimiting("account-write")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.RegisterAsync(request.Email, request.Password, cancellationToken);
        if (result.Errors.Length > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]> { ["Registration"] = result.Errors }));
        }
        return Accepted(new { message = AcceptedMessage });
    }

    /// <summary>Opens a safe confirmation form; following an email link never updates the account on GET.</summary>
    [HttpGet("confirm-email"), AllowAnonymous, EnableRateLimiting("account-confirm")]
    public IActionResult ConfirmationPage([FromQuery] ConfirmEmailRequest request)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        var encode = HtmlEncoder.Default;
        // Encode every attribute, including the form token, and post to this same application origin.
        var action = encode.Encode(Request.PathBase + "/api/v1/auth/confirm-email");
        var html = "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Confirm DocuMind email</title>" +
            "<body><h1>Confirm your email address</h1><p>Submit this form to confirm your DocuMind account.</p>" +
            $"<form method=\"post\" action=\"{action}\">" +
            $"<input type=\"hidden\" name=\"userId\" value=\"{encode.Encode(request.UserId)}\">" +
            $"<input type=\"hidden\" name=\"token\" value=\"{encode.Encode(request.Token)}\">" +
            $"<input type=\"hidden\" name=\"{encode.Encode(tokens.FormFieldName)}\" value=\"{encode.Encode(tokens.RequestToken!)}\">" +
            "<button type=\"submit\">Confirm email</button></form></body></html>";
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>Confirms an email from a CSRF-protected JSON request, suitable for the Next.js client.</summary>
    [HttpPost("confirm-email"), AllowAnonymous, Consumes("application/json"), EnableRateLimiting("account-confirm")]
    public Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest request, CancellationToken cancellationToken)
        => ConfirmEmailResponseAsync(request, cancellationToken);

    /// <summary>Handles the confirmation form served by the safe email-link GET endpoint.</summary>
    [HttpPost("confirm-email"), AllowAnonymous, Consumes("application/x-www-form-urlencoded"), EnableRateLimiting("account-confirm")]
    public Task<IActionResult> ConfirmEmailForm([FromForm] ConfirmEmailRequest request, CancellationToken cancellationToken)
        => ConfirmEmailResponseAsync(request, cancellationToken);

    /// <summary>Requests another confirmation email with the same response for all valid addresses.</summary>
    [HttpPost("resend-confirmation"), AllowAnonymous, Consumes("application/json"), EnableRateLimiting("account-write")]
    public async Task<IActionResult> ResendConfirmation(ResendConfirmationRequest request, CancellationToken cancellationToken)
    {
        await accounts.ResendConfirmationAsync(request.Email, cancellationToken);
        return Accepted(new { message = AcceptedMessage });
    }

    /// <summary>Signs in with Identity; wrong passwords, unknown/unconfirmed accounts, and lockout look alike.</summary>
    [HttpPost("login"), AllowAnonymous, Consumes("application/json"), EnableRateLimiting("account-session")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        if (!await sessions.LoginAsync(request.Email, request.Password, request.RememberMe, cancellationToken))
        {
            return Unauthorized(new { message = "Unable to sign in. Check your credentials and try again." });
        }
        return NoContent();
    }

    /// <summary>Clears the signed-in browser's session; authentication and a matching CSRF token are required.</summary>
    [HttpPost("logout"), EnableRateLimiting("account-session")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await sessions.LogoutAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Returns an explicit projection of the current account, excluding hashes, stamps, and lockout details.</summary>
    [HttpGet("me"), EnableRateLimiting("account-bootstrap")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var account = await sessions.GetCurrentAccountAsync(User, cancellationToken);
        return account is null ? Unauthorized() : Ok(account);
    }

    /// <summary>Shares HTTP confirmation behavior between JSON and the email-link form submission.</summary>
    private async Task<IActionResult> ConfirmEmailResponseAsync(ConfirmEmailRequest request, CancellationToken cancellationToken)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!await accounts.ConfirmEmailAsync(request.UserId, request.Token, cancellationToken))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid or expired confirmation link.",
                Detail = "Request a new confirmation email and use its link."
            });
        }
        return Ok(new { message = "Your email address is confirmed." });
    }
}

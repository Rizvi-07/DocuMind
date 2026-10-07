using DocuMind.Api.Contracts;
using DocuMind.Services.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DocuMind.Api.Controllers;

/// <summary>Maps account use cases to HTTP responses; Identity and delivery remain in Services.</summary>
[ApiController, AllowAnonymous, Route("api/v1/auth"), RequestSizeLimit(16 * 1024)]
public sealed class AuthController(IAccountService accounts) : ControllerBase
{
    // Identical status and body are used for new, duplicate, and unknown eligible addresses.
    private const string AcceptedMessage =
        "If this address is eligible, a confirmation email will be sent. Check your inbox.";

    /// <summary>Accepts valid registration input without disclosing account existence or signing in.</summary>
    [HttpPost("register"), Consumes("application/json"), EnableRateLimiting("account-write")]
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

    /// <summary>Confirms a valid token or provides one clear response for invalid and expired links.</summary>
    [HttpGet("confirm-email"), EnableRateLimiting("account-confirm")]
    public async Task<IActionResult> ConfirmEmail([FromQuery] ConfirmEmailRequest request,
        CancellationToken cancellationToken)
    {
        // Confirmation responses are sensitive and must not be stored by intermediary caches.
        Response.Headers.CacheControl = "no-store";
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

    /// <summary>Requests another confirmation email with the same response for all valid addresses.</summary>
    [HttpPost("resend-confirmation"), Consumes("application/json"), EnableRateLimiting("account-write")]
    public async Task<IActionResult> ResendConfirmation(ResendConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        await accounts.ResendConfirmationAsync(request.Email, cancellationToken);
        return Accepted(new { message = AcceptedMessage });
    }
}

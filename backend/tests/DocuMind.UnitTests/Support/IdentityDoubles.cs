using DocuMind.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DocuMind.UnitTests.Support;

/// <summary>Constructs strict Identity doubles so tests fail on unexpected account or sign-in operations.</summary>
internal sealed class IdentityDoubles
{
    public Mock<IPasswordHasher<ApplicationUser>> Hasher { get; } = new(MockBehavior.Strict);
    public Mock<UserManager<ApplicationUser>> Users { get; }
    public Mock<SignInManager<ApplicationUser>> SignIn { get; }

    /// <summary>Supplies Identity's constructor dependencies without a database, real passwords, or cookies.</summary>
    public IdentityDoubles()
    {
        var options = Options.Create(new IdentityOptions());
        Users = new Mock<UserManager<ApplicationUser>>(MockBehavior.Strict,
            Mock.Of<IUserStore<ApplicationUser>>(), options, Hasher.Object,
            Array.Empty<IUserValidator<ApplicationUser>>(), Array.Empty<IPasswordValidator<ApplicationUser>>(),
            Mock.Of<ILookupNormalizer>(), new IdentityErrorDescriber(), Mock.Of<IServiceProvider>(),
            NullLogger<UserManager<ApplicationUser>>.Instance);
        // Identity constructors assign virtual properties; allow that setup before enforcing method expectations.
        Users.SetupAllProperties();
        SignIn = new Mock<SignInManager<ApplicationUser>>(MockBehavior.Strict,
            Users.Object, new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Mock.Of<IUserClaimsPrincipalFactory<ApplicationUser>>(), options,
            NullLogger<SignInManager<ApplicationUser>>.Instance,
            Mock.Of<IAuthenticationSchemeProvider>(), Mock.Of<IUserConfirmation<ApplicationUser>>());
        SignIn.SetupAllProperties();
        _ = SignIn.Object;
        // Ignore constructor property assignments when verifying that canceled operations made no calls.
        Users.Invocations.Clear();
        SignIn.Invocations.Clear();
    }
}

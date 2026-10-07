using Microsoft.AspNetCore.Identity;

namespace DocuMind.Data.Entities;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
        Id = Guid.NewGuid();
    }

    public DateTimeOffset CreatedAt { get; set; }
        = DateTimeOffset.UtcNow;
}
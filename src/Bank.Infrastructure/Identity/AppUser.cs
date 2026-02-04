using Microsoft.AspNetCore.Identity;

namespace Bank.Infrastructure.Identity;

public class AppUser : IdentityUser
{
    public string? FirstName { get; set; }
    public string? LastName  { get; set; }

    // PhoneNumber already exists on IdentityUser, so it is NOT re-added it here.

    public string? Address { get; set; }
}
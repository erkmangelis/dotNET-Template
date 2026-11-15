using Microsoft.AspNetCore.Identity;

namespace Acme.Notes.Api.Features.Auth;

/// <summary>An account. Add profile fields here; Identity owns the rest.</summary>
internal sealed class AppUser : IdentityUser;

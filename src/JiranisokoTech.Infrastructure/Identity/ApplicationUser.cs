using JiranisokoTech.Domain.Audit;
using Microsoft.AspNetCore.Identity;

namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>
/// An account: how somebody signs in, and who the system believes they are.
/// </summary>
/// <remarks>
/// Deliberately in Infrastructure rather than Domain. It inherits from a
/// framework type, and the domain is meant to be testable without ASP.NET,
/// Identity or a database in the room. Business objects refer to a person by
/// <see cref="Guid"/>; the employee record, with contracts and salary and a
/// manager, is a separate thing in the People module that happens to point at
/// an account.
///
/// That separation is not ceremony. Not every account is an employee — service
/// accounts and, later, client logins — and not every employee needs an
/// account. Modelling them as one row makes both cases awkward forever.
/// </remarks>
public sealed class ApplicationUser : IdentityUser<Guid>, IAuditable
{
    public ApplicationUser()
    {
        Id = Guid.CreateVersion7();
        DisplayName = string.Empty;
    }

    public ApplicationUser(string email, string displayName) : this()
    {
        UserName = email;
        Email = email;
        DisplayName = displayName;
    }

    /// <summary>What to call them. Copied into audit entries as they act.</summary>
    public string DisplayName { get; set; }

    public string? JobTitle { get; set; }

    /// <summary>
    /// Where they are, as an IANA zone such as <c>Africa/Nairobi</c>.
    /// </summary>
    /// <remarks>
    /// Storage is UTC everywhere. This exists so a screen can show somebody a
    /// deadline in the hours they actually work, which is a rendering decision
    /// and never a storage one.
    /// </remarks>
    public string TimeZone { get; set; } = "Africa/Nairobi";

    /// <summary>
    /// Whether the account may sign in at all.
    /// </summary>
    /// <remarks>
    /// Separate from Identity's lockout, which is a temporary consequence of
    /// getting a password wrong. This is a decision somebody made: a leaver, a
    /// suspension. Deactivating rather than deleting keeps their name on
    /// everything they did — a trail that renders "unknown user" for a former
    /// employee's work has lost the part people read.
    /// </remarks>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSignedInAt { get; set; }

    /// <summary>
    /// Columns the audit trail must never copy.
    /// </summary>
    /// <remarks>
    /// Two different reasons, both deliberate.
    ///
    /// The first group are credentials and tokens. The audit table is read by
    /// more people than this one, so a secret copied into it has leaked
    /// sideways — and the copy would outlive a password change, because audit
    /// entries are append-only by design.
    ///
    /// The second group is sign-in churn. Every successful sign-in moves
    /// <see cref="LastSignedInAt"/> and every failure moves
    /// <see cref="IdentityUser{TKey}.AccessFailedCount"/>, so auditing them
    /// would put one entry per login attempt into the trail and bury the
    /// changes people actually search it for. That story is already told, in
    /// full and with the address it came from, in
    /// <see cref="SignInRecord"/>.
    ///
    /// <see cref="IdentityUser{TKey}.LockoutEnd"/> is not excluded. It moves
    /// rarely, it can also be set by an administrator deliberately, and that
    /// is exactly the sort of act the trail is for.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>
    {
        nameof(PasswordHash),
        nameof(SecurityStamp),
        nameof(ConcurrencyStamp),
        nameof(TwoFactorEnabled),
        nameof(NormalizedEmail),
        nameof(NormalizedUserName),

        nameof(LastSignedInAt),
        nameof(AccessFailedCount),
    };
}

/// <summary>A named bundle of permissions. The permissions live on its claims.</summary>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() => Id = Guid.CreateVersion7();

    public ApplicationRole(string name) : this()
    {
        Name = name;
        NormalizedName = name.ToUpperInvariant();
    }

    /// <summary>What this role is for, in words, for whoever assigns it.</summary>
    public string? Description { get; set; }
}

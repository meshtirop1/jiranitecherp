using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>
/// Identity's store, with the second-factor secrets encrypted before they reach the database.
/// </summary>
/// <remarks>
/// <b>Identity keeps them in plain text by default.</b> The authenticator key — the seed every
/// six-digit code is computed from — and the unused recovery codes are rows in the user tokens
/// table, written as they are. A copy of the database, from a backup or a leaked dump, was
/// therefore a working second factor for every account that had one, which is exactly the
/// credential a second factor exists to be independent of. Section 28 of the brief says never
/// to store a raw secret unnecessarily, and nothing about keeping these readable is necessary.
///
/// They are encrypted with the application's key ring, the same one that signs cookies and
/// protects webhook subscription secrets, under a purpose string of their own. That key ring
/// lives on a volume apart from the database, which is the whole argument: either one alone
/// yields nothing. It also means <b>losing the key ring turns every second factor off in
/// effect</b> — the codes cannot be checked — so docs/deployment.md says to back it up, and not
/// alongside the database.
///
/// Overridden at the token level rather than at the authenticator and recovery-code methods,
/// because both of those reach the table through <see cref="SetTokenAsync"/> and
/// <see cref="GetTokenAsync"/>, and a third internal token added by a future version of Identity
/// is protected without anybody noticing it exists.
/// </remarks>
public sealed class ProtectedUserStore(
    AppDbContext context,
    IDataProtectionProvider protection,
    ILogger<ProtectedUserStore> logger,
    IdentityErrorDescriber? describer = null)
    : UserStore<ApplicationUser, ApplicationRole, AppDbContext, Guid>(context, describer)
{
    /// <summary>The login provider Identity files its own tokens under.</summary>
    public const string InternalProvider = "[AspNetUserStore]";

    /// <summary>
    /// Written before every protected value, so a value from before this existed is recognised.
    /// </summary>
    /// <remarks>
    /// Without it, a stored raw key would be handed to Unprotect, fail, and read as "no key" —
    /// and everybody enrolled before the upgrade would find their codes refused.
    /// <see cref="ProtectExistingAsync"/> converts those at startup; the marker is what makes the
    /// window before it runs, and a row it could not convert, harmless.
    /// </remarks>
    public const string Marker = "dp1:";

    private readonly IDataProtector _protector =
        protection.CreateProtector("JiranisokoTech.Identity.SecondFactor.v1");

    public override Task SetTokenAsync(
        ApplicationUser user, string loginProvider, string name, string? value,
        CancellationToken cancellationToken)
    {
        if (loginProvider == InternalProvider && value is not null)
        {
            value = Protect(value);
        }

        return base.SetTokenAsync(user, loginProvider, name, value, cancellationToken);
    }

    public override async Task<string?> GetTokenAsync(
        ApplicationUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var stored = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);

        if (loginProvider != InternalProvider || stored is null || !stored.StartsWith(Marker, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            return _protector.Unprotect(stored[Marker.Length..]);
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            /*
             * The key ring this was written with is gone. Answering "no key" makes a code
             * fail to verify, which is the truth; throwing would put an error screen in the
             * middle of somebody signing in, over and over, with nothing they could do about
             * it. Logged as an error because it is one: every enrolled account is affected,
             * and the fix is restoring the key ring, not anything on this account.
             */
            logger.LogError(
                exception,
                "A second-factor secret for account {AccountId} could not be decrypted; the key ring it was written with is missing",
                user.Id);

            return null;
        }
    }

    private string Protect(string value) =>
        value.StartsWith(Marker, StringComparison.Ordinal) ? value : Marker + _protector.Protect(value);

    /// <summary>
    /// Encrypt any second-factor secret still stored as it was written before this store.
    /// </summary>
    /// <returns>How many were converted.</returns>
    /// <remarks>
    /// Run at startup. Idempotent: a converted row carries the marker and is skipped, so on
    /// every start after the first this reads the rows and writes nothing.
    /// </remarks>
    public async Task<int> ProtectExistingAsync(CancellationToken cancellationToken = default)
    {
        var raw = await Context.UserTokens
            .Where(token => token.LoginProvider == InternalProvider
                && token.Value != null
                && !token.Value.StartsWith(Marker))
            .ToListAsync(cancellationToken);

        foreach (var token in raw)
        {
            token.Value = Protect(token.Value!);
        }

        await Context.SaveChangesAsync(cancellationToken);

        return raw.Count;
    }
}

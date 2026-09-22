using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// Signing somebody in, and recording that it was attempted.
/// </summary>
/// <remarks>
/// One place, so every attempt is recorded whatever called it. Spreading this
/// across the pages that need it guarantees that the third one forgets, and the
/// gap is invisible: sign-in still works, and only the history is wrong.
/// </remarks>
public sealed class SignInService(
    ApplicationSignInManager signInManager,
    UserManager<ApplicationUser> users,
    AppDbContext database,
    IClock clock)
{
    /// <summary>
    /// Attempt a password sign-in.
    /// </summary>
    /// <remarks>
    /// The caller is told the outcome so it can act — a locked account needs a
    /// different message from a wrong password — but it is not told which to
    /// <em>show</em>. That decision belongs at the page, and the page says the
    /// same thing for every refusal.
    /// </remarks>
    public async Task<SignInOutcome> PasswordSignInAsync(
        string email,
        string password,
        bool remember,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var user = await users.FindByEmailAsync(email);

        if (user is null)
        {
            /*
             * No account. Recorded, because a run of these against different
             * addresses is the shape of somebody working through a list, and
             * that is only visible if the misses are kept as well as the hits.
             */
            await RecordAsync(null, email, SignInOutcome.Refused, ipAddress, userAgent, cancellationToken);

            return SignInOutcome.Refused;
        }

        var result = await signInManager.PasswordSignInAsync(user, password, remember, lockoutOnFailure: true);

        var outcome = result switch
        {
            { Succeeded: true } => SignInOutcome.Succeeded,
            { IsLockedOut: true } => SignInOutcome.LockedOut,
            { RequiresTwoFactor: true } => SignInOutcome.SecondFactorRequired,

            // Identity reports a deactivated account as "not allowed", because
            // CanSignInAsync refused it. Named properly here so the history
            // says what actually happened.
            { IsNotAllowed: true } when !user.IsActive => SignInOutcome.Deactivated,
            _ => SignInOutcome.Refused,
        };

        if (outcome == SignInOutcome.Succeeded)
        {
            user.LastSignedInAt = clock.Now;
            await users.UpdateAsync(user);
        }

        await RecordAsync(user.Id, email, outcome, ipAddress, userAgent, cancellationToken);

        return outcome;
    }

    /// <summary>
    /// Record that a second factor finished a sign-in.
    /// </summary>
    /// <remarks>
    /// Without this the history of a two-factor account reads "password
    /// accepted, second step owed" and then nothing, for every successful
    /// sign-in they ever make — which is the opposite of what the page is for.
    ///
    /// A recovery code is recorded as itself. Somebody reading their own
    /// history needs to see that one was spent, because a recovery code used by
    /// anybody other than them is the clearest sign there is that the account
    /// has gone.
    /// </remarks>
    public async Task RecordSecondFactorAsync(
        ApplicationUser user,
        bool byRecoveryCode,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        user.LastSignedInAt = clock.Now;
        await users.UpdateAsync(user);

        await RecordAsync(
            user.Id,
            user.Email ?? string.Empty,
            byRecoveryCode ? SignInOutcome.RecoveryCodeUsed : SignInOutcome.Succeeded,
            ipAddress,
            userAgent,
            cancellationToken);
    }

    /// <summary>What this account has seen, most recent first.</summary>
    public Task<List<SignInRecord>> HistoryFor(Guid userId, int take = 20, CancellationToken cancellationToken = default) =>
        database.Set<SignInRecord>()
            .Where(record => record.UserId == userId)
            .OrderByDescending(record => record.At)
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task SignOutAsync() => signInManager.SignOutAsync();

    private async Task RecordAsync(
        Guid? userId,
        string email,
        SignInOutcome outcome,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        database.Set<SignInRecord>().Add(
            SignInRecord.For(userId, email, outcome, clock.Now, ipAddress, userAgent));

        await database.SaveChangesAsync(cancellationToken);
    }
}

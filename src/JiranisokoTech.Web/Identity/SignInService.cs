using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Domain.Audit;
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
    SignInPlaces places,
    IClock clock,
    ILogger<SignInService> logger)
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

    /// <summary>
    /// End every session this account has, including the one asking.
    /// </summary>
    /// <remarks>
    /// Rolling the security stamp is the entire mechanism, and it is the only
    /// one available: a session is a signed cookie held by a browser, nothing in
    /// this system knows how many exist or where they are, and there is no list
    /// to walk. Every cookie already issued carries the old stamp, so each one
    /// dies the next time it is validated against the database — which the
    /// interval in <c>IdentityConfiguration</c> bounds. That is why a page
    /// offering this cannot promise the sessions are gone by the time it has
    /// finished rendering.
    ///
    /// This browser is signed out here and now rather than left to notice on its
    /// own. Identity can carry the current session across a stamp roll by
    /// re-issuing its cookie, and most systems do exactly that, but then the
    /// page has to promise "everywhere except the browser you happen to be
    /// holding" — and somebody who has just seen a sign-in from an address they
    /// do not recognise is the last person who should have to work out which
    /// browser that sentence excludes. Ending it also proves the button did
    /// something, because they land on the sign-in page, and signing back in
    /// answers the question they ask next: whether their password still works.
    /// </remarks>
    public async Task EndEverySessionAsync(ApplicationUser user)
    {
        await users.UpdateSecurityStampAsync(user);
        await signInManager.SignOutAsync();

        /*
         * The only trace this leaves. A stamp is excluded from the audit trail
         * because it also moves on every password change, and the sessions it
         * kills were never recorded anywhere to begin with — so somebody asking
         * next week why the whole office was signed out at four o'clock has this
         * line or has nothing.
         */
        logger.LogInformation("Ended every session for account {UserId}.", user.Id);
    }

    private async Task RecordAsync(
        Guid? userId,
        string email,
        SignInOutcome outcome,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        /*
         * Asked before the record is written, because the answer is "has this account
         * been used here before" and writing this sign-in first makes the answer yes.
         */
        var somewhereNew = outcome == SignInOutcome.Succeeded && userId is { } account
            && await places.IsSomewhereNewAsync(account, ipAddress, userAgent, cancellationToken);

        database.Set<SignInRecord>().Add(
            SignInRecord.For(userId, email, outcome, clock.Now, ipAddress, userAgent));

        if (somewhereNew)
        {
            /*
             * Told through the outbox rather than sent here. A mail server that is slow
             * or down must not make signing in slow or impossible — and the one moment a
             * person must be able to get in is the moment something has gone wrong with
             * their account.
             *
             * A notice, never a block. This fires for a new laptop as readily as for a
             * stolen password, and refusing the sign-in would lock people out of their own
             * accounts on the day they buy a machine.
             */
            logger.LogInformation(
                "Account {UserId} signed in from somewhere it has not been used before.",
                userId);

            var told = new SignedInSomewhereNew(
                userId!.Value,
                email,
                SignInPlaces.Describe(userAgent),
                ipAddress ?? "an unknown address",
                clock.Now);

            /*
             * Written onto the outbox by hand, which is the only place in this system
             * that happens. Every other event is raised by an aggregate and collected on
             * save, and signing in is not a change to a business record — so there is no
             * aggregate to raise it.
             */
            database.Announce(told);
        }

        await database.SaveChangesAsync(cancellationToken);
    }
}

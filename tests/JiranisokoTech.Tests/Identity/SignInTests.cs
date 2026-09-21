using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// What happens when somebody tries to sign in, and what is written down.
/// </summary>
public class SignInTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task The_right_password_signs_somebody_in()
    {
        var user = await factory.CreateAccountAsync("correct@jiranisokotech.co.ke", Password);

        var outcome = await AttemptAsync("correct@jiranisokotech.co.ke", Password);

        Assert.Equal(SignInOutcome.Succeeded, outcome);

        var record = await LastRecordFor("correct@jiranisokotech.co.ke");

        Assert.Equal(SignInOutcome.Succeeded, record.Outcome);
        Assert.Equal(user.Id, record.UserId);
    }

    [Fact]
    public async Task A_successful_sign_in_is_stamped_on_the_account()
    {
        await factory.CreateAccountAsync("stamped@jiranisokotech.co.ke", Password);

        await AttemptAsync("stamped@jiranisokotech.co.ke", Password);

        var stored = await FindAsync("stamped@jiranisokotech.co.ke");

        Assert.NotNull(stored.LastSignedInAt);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        await factory.CreateAccountAsync("wrong@jiranisokotech.co.ke", Password);

        var outcome = await AttemptAsync("wrong@jiranisokotech.co.ke", "not-the-password");

        Assert.Equal(SignInOutcome.Refused, outcome);
        Assert.Equal(SignInOutcome.Refused, (await LastRecordFor("wrong@jiranisokotech.co.ke")).Outcome);
    }

    /// <summary>
    /// The misses matter more than the hits: a run of attempts against
    /// addresses that do not exist is the shape of somebody working through a
    /// list, and it is only visible if the misses are kept.
    /// </summary>
    [Fact]
    public async Task An_address_that_matches_nothing_is_still_recorded()
    {
        var outcome = await AttemptAsync("nobody@example.com", Password);

        Assert.Equal(SignInOutcome.Refused, outcome);

        var record = await LastRecordFor("nobody@example.com");

        Assert.Null(record.UserId);
        Assert.Equal("nobody@example.com", record.Email);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await factory.CreateAccountAsync("locked@jiranisokotech.co.ke", Password);

        var outcomes = new List<SignInOutcome>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            outcomes.Add(await AttemptAsync("locked@jiranisokotech.co.ke", "not-the-password"));
        }

        Assert.Equal(SignInOutcome.LockedOut, outcomes[^1]);

        // And the lock is about the account, not the attempt: the right
        // password does not open it either, which is the whole point.
        Assert.Equal(
            SignInOutcome.LockedOut,
            await AttemptAsync("locked@jiranisokotech.co.ke", Password));
    }

    /// <summary>
    /// A deactivated account is refused even with the correct password, and the
    /// refusal is named as itself rather than lumped in with a bad password —
    /// in the record, though never in what the page says back.
    /// </summary>
    [Fact]
    public async Task A_withdrawn_account_cannot_sign_in_with_the_right_password()
    {
        await factory.CreateAccountAsync(
            "leaver@jiranisokotech.co.ke", Password, active: false);

        var outcome = await AttemptAsync("leaver@jiranisokotech.co.ke", Password);

        Assert.Equal(SignInOutcome.Deactivated, outcome);
        Assert.Equal(
            SignInOutcome.Deactivated,
            (await LastRecordFor("leaver@jiranisokotech.co.ke")).Outcome);
    }

    [Fact]
    public async Task A_history_is_one_persons_own_and_newest_first()
    {
        var mine = await factory.CreateAccountAsync("mine@jiranisokotech.co.ke", Password);
        await factory.CreateAccountAsync("theirs@jiranisokotech.co.ke", Password);

        await AttemptAsync("mine@jiranisokotech.co.ke", "not-the-password");
        await AttemptAsync("mine@jiranisokotech.co.ke", Password);
        await AttemptAsync("theirs@jiranisokotech.co.ke", Password);

        var history = await factory.InRequestAsync(services =>
            services.GetRequiredService<SignInService>().HistoryFor(mine.Id));

        Assert.Equal(2, history.Count);
        Assert.All(history, record => Assert.Equal(mine.Id, record.UserId));

        // Newest first, so the page shows the most recent attempt at the top
        // without the caller having to know to reverse it.
        Assert.Equal(SignInOutcome.Succeeded, history[0].Outcome);
        Assert.Equal(SignInOutcome.Refused, history[1].Outcome);
    }

    /// <summary>
    /// Sign-in belongs in its own table, not in the audit trail.
    /// </summary>
    /// <remarks>
    /// The account row does move on a successful sign-in — a timestamp, a reset
    /// failure count — and auditing those would put an entry in the trail for
    /// every login in the firm, burying the changes people open it to find.
    /// This is the test that keeps the exclusion honest; without it somebody
    /// removes a name from AuditExcludes and nothing complains.
    /// </remarks>
    [Fact]
    public async Task Signing_in_does_not_fill_the_audit_trail()
    {
        var user = await factory.CreateAccountAsync("quiet@jiranisokotech.co.ke", Password);

        var before = await AuditEntriesFor(user.Id);

        await AttemptAsync("quiet@jiranisokotech.co.ke", Password);
        await AttemptAsync("quiet@jiranisokotech.co.ke", "not-the-password");

        Assert.Equal(before, await AuditEntriesFor(user.Id));
    }

    private Task<SignInOutcome> AttemptAsync(string email, string password) =>
        factory.InRequestAsync(services =>
            services.GetRequiredService<SignInService>().PasswordSignInAsync(
                email, password, remember: false, ipAddress: "198.51.100.7", userAgent: "xunit"));

    private async Task<SignInRecord> LastRecordFor(string email)
    {
        SignInRecord? found = null;

        await factory.InScopeAsync(async services =>
        {
            found = await services.GetRequiredService<AppDbContext>()
                .Set<SignInRecord>()
                .Where(record => record.Email == email)
                .OrderByDescending(record => record.At)
                .FirstOrDefaultAsync();
        });

        Assert.NotNull(found);

        return found!;
    }

    private async Task<ApplicationUser> FindAsync(string email)
    {
        ApplicationUser? found = null;

        await factory.InScopeAsync(async services =>
        {
            found = await services.GetRequiredService<UserManager<ApplicationUser>>()
                .FindByEmailAsync(email);
        });

        Assert.NotNull(found);

        return found!;
    }

    private async Task<int> AuditEntriesFor(Guid subjectId)
    {
        var count = 0;

        await factory.InScopeAsync(async services =>
        {
            count = await services.GetRequiredService<AppDbContext>()
                .Set<AuditEntry>()
                .CountAsync(entry => entry.SubjectId == subjectId);
        });

        return count;
    }
}

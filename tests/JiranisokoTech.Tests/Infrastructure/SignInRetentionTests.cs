using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Scheduling;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Web.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using JiranisokoTech.Infrastructure.Persistence;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// What the sign-in sweep takes, and the far more important question of what it leaves.
/// </summary>
/// <remarks>
/// <c>SignInRecord</c> has said since the day it was written that it is "its own table with
/// its own retention", and it had none — while being the one table in this database that a
/// stranger on the public internet can add rows to as fast as they like.
///
/// The sweep is trivial. The reason these tests exist is the other half: this table is not
/// only a record of what happened, it is the evidence another feature reads. Successes are
/// what <c>SignInPlaces</c> compares a sign-in against to decide whether somebody should be
/// warned that their account has been used somewhere unfamiliar — and that check returns
/// false when there is nothing to compare against, so a sweep that took successes would
/// switch the warning off per account, silently, for good. Nothing would report it and no
/// screen would look different.
/// </remarks>
public class SignInRetentionTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    /// <summary>Old failures go. That is the whole of what this job is for.</summary>
    [Fact]
    public async Task A_failed_attempt_old_enough_to_prove_nothing_is_deleted()
    {
        var address = "198.51.100.90";

        await RecordAsync(SignInOutcome.Refused, address, DateTimeOffset.UtcNow.AddDays(-120));
        await RecordAsync(SignInOutcome.Refused, address, DateTimeOffset.UtcNow.AddDays(-2));

        var said = await SweepAsync();

        Assert.Contains("Deleted 1", said);
        Assert.Equal(1, await CountAsync(address));
    }

    /// <summary>
    /// Successes are never deleted, however old.
    /// </summary>
    /// <remarks>
    /// The assertion the job exists around rather than the one it exists for. A sweep written
    /// the obvious way — everything older than ninety days — passes every other test here and
    /// takes the unfamiliar-sign-in warning with it.
    /// </remarks>
    [Fact]
    public async Task A_successful_sign_in_is_never_deleted_however_old()
    {
        var address = "198.51.100.91";

        await RecordAsync(SignInOutcome.Succeeded, address, DateTimeOffset.UtcNow.AddYears(-4));
        await RecordAsync(
            SignInOutcome.RecoveryCodeUsed, address, DateTimeOffset.UtcNow.AddYears(-4));

        await SweepAsync();

        Assert.Equal(2, await CountAsync(address));
    }

    /// <summary>
    /// Recent failures stay, because the throttle counts them.
    /// </summary>
    /// <remarks>
    /// Ninety days against a fifteen-minute window is an enormous margin, and stating it is
    /// still worth the four lines: the two numbers live in different files and the one that
    /// matters is invisible from the other. A retention shortened to "an hour" one afternoon
    /// would disable <c>SignInThrottle</c> and nothing else in the suite would notice.
    /// </remarks>
    [Fact]
    public async Task Failures_the_throttle_still_counts_are_left_alone()
    {
        var address = "198.51.100.92";

        await RecordAsync(SignInOutcome.Refused, address, DateTimeOffset.UtcNow.AddMinutes(-5));

        await SweepAsync();

        Assert.Equal(1, await CountAsync(address));
        Assert.True(SignInThrottle.Window < PruneSignInHistory.KeepFailuresFor);
    }

    /// <summary>
    /// After a sweep, an account is still warned about somewhere it has not been.
    /// </summary>
    /// <remarks>
    /// The end-to-end version, and the one that would catch a sweep written against the wrong
    /// column. It is asserted in both directions on purpose: the familiar place is still
    /// familiar and the unfamiliar one is still unfamiliar, because a sweep that removed the
    /// history entirely would make <i>everywhere</i> familiar — <c>IsSomewhereNewAsync</c>
    /// returns false when an account has nothing to be unfamiliar against — and a test that
    /// only asserted the first half would pass while the warning was dead.
    /// </remarks>
    [Fact]
    public async Task The_unfamiliar_sign_in_warning_still_works_after_a_sweep()
    {
        var account = await factory.CreateAccountAsync(
            "swept@jiranisokotech.co.ke", "a-long-enough-password");

        const string desk = "198.51.100.93";
        const string browser = "Mozilla/5.0 (Windows NT 10.0) Firefox/141.0";

        // Two successes from one place, old enough that a naive sweep would take them, plus
        // the failures that same desk accumulated on the mornings somebody mistyped.
        await RecordAsync(
            SignInOutcome.Succeeded, desk, DateTimeOffset.UtcNow.AddDays(-400), account.Id, browser);
        await RecordAsync(
            SignInOutcome.Succeeded, desk, DateTimeOffset.UtcNow.AddDays(-380), account.Id, browser);
        await RecordAsync(
            SignInOutcome.Refused, desk, DateTimeOffset.UtcNow.AddDays(-390), account.Id, browser);

        await SweepAsync();

        await factory.InScopeAsync(async services =>
        {
            var places = services.GetRequiredService<SignInPlaces>();

            Assert.False(
                await places.IsSomewhereNewAsync(account.Id, desk, browser),
                "The desk this account has used twice is being reported as somewhere new.");

            Assert.True(
                await places.IsSomewhereNewAsync(
                    account.Id, "203.0.113.200", "Mozilla/5.0 (Macintosh) Safari/18.0"),
                "An address and browser this account has never used is not being reported "
                + "as new, which means the warning has stopped working rather than that the "
                + "place is familiar.");
        });
    }

    private async Task<string> SweepAsync()
    {
        var said = string.Empty;

        await factory.InScopeAsync(async services =>
        {
            var job = services.GetServices<IRecurringJob>()
                .Single(one => one.Name == "sign-ins.prune");

            said = await job.RunAsync();
        });

        return said;
    }

    private Task RecordAsync(
        SignInOutcome outcome,
        string address,
        DateTimeOffset at,
        Guid? userId = null,
        string? userAgent = null) =>
        factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var record = SignInRecord.For(
                userId, "somebody@example.com", outcome, at, address, userAgent ?? "xunit");

            database.Set<SignInRecord>().Add(record);
            await database.SaveChangesAsync();
        });

    private async Task<int> CountAsync(string address)
    {
        var count = 0;

        await factory.InScopeAsync(async services =>
        {
            count = await services.GetRequiredService<AppDbContext>()
                .Set<SignInRecord>()
                .CountAsync(record => record.IpAddress == address);
        });

        return count;
    }
}

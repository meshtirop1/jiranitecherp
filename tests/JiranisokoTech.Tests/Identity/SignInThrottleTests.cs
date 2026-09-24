using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Web.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// How many times one address may fail before it is asked to wait.
/// </summary>
/// <remarks>
/// Sign-in was the one public form in this application with no rate limit of any kind. The
/// careers form and the recovery form have each had one since they were written; the form that
/// guards the accounts had only Identity's lockout, which counts failures <b>per account</b>.
///
/// That is the wrong axis for the attack this firm is exposed to. Somebody with a list of
/// addresses and the twenty commonest passwords tries one password against every address, then
/// the next — so each account sees a single failure between attempts hours apart and no
/// lockout ever fires. Password spraying is exactly what a per-account lockout is blind to.
///
/// <b>Every test here uses its own address.</b> The throttle counts across the whole class's
/// shared database, so two tests sharing an address would either interfere or, worse, pass for
/// the wrong reason once somebody added a third.
/// </remarks>
public class SignInThrottleTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    /// <summary>
    /// An address that has failed too often is asked to wait, whatever it tries next.
    /// </summary>
    /// <remarks>
    /// The last attempt uses the <i>correct</i> password on purpose. That is what proves the
    /// throttle runs before the credentials are checked at all — the whole point, because a
    /// throttle that lets the password be tested first has not stopped the guessing, it has
    /// only stopped the reply.
    /// </remarks>
    [Fact]
    public async Task An_address_that_has_failed_too_often_is_refused_even_with_the_right_password()
    {
        const string address = "203.0.113.11";

        await factory.CreateAccountAsync("spray-target@jiranisokotech.co.ke", Password);

        for (var attempt = 0; attempt < SignInThrottle.MostFailures; attempt++)
        {
            // A different address each time, which is the shape of a spray: no single account
            // accumulates enough failures for Identity's lockout to notice.
            var outcome = await AttemptAsync(
                $"nobody-{attempt}@example.com", "guessed-password", address);

            Assert.Equal(SignInOutcome.Refused, outcome);
        }

        var throttled = await AttemptAsync(
            "spray-target@jiranisokotech.co.ke", Password, address);

        Assert.Equal(SignInOutcome.TooManyAttempts, throttled);
    }

    /// <summary>
    /// A throttled attempt is not written down.
    /// </summary>
    /// <remarks>
    /// Recording it would hand whoever triggered the throttle a way of growing this table at
    /// whatever rate they liked — and there is nothing to learn from the row: the fifteen
    /// failures that caused the refusal are already in the history, and this one never reached
    /// the password check.
    /// </remarks>
    [Fact]
    public async Task A_throttled_attempt_adds_nothing_to_the_history()
    {
        const string address = "203.0.113.12";

        for (var attempt = 0; attempt < SignInThrottle.MostFailures; attempt++)
        {
            await AttemptAsync($"nothing-{attempt}@example.com", "guessed", address);
        }

        var before = await RecordsFrom(address);

        Assert.Equal(
            SignInOutcome.TooManyAttempts,
            await AttemptAsync("nothing-again@example.com", "guessed", address));

        Assert.Equal(before, await RecordsFrom(address));
    }

    /// <summary>
    /// Successes do not count against an address, however many there are.
    /// </summary>
    /// <remarks>
    /// Everybody in this firm is behind one address. A throttle that counted successful
    /// sign-ins would lock the office out of its own application on a Monday morning, and a
    /// throttle that does that gets switched off — which is worse than not having one.
    /// </remarks>
    [Fact]
    public async Task Signing_in_correctly_never_counts_against_the_address()
    {
        const string address = "203.0.113.13";

        await factory.CreateAccountAsync("busy-office@jiranisokotech.co.ke", Password);

        for (var attempt = 0; attempt < SignInThrottle.MostFailures + 5; attempt++)
        {
            var outcome = await AttemptAsync(
                "busy-office@jiranisokotech.co.ke", Password, address);

            Assert.Equal(SignInOutcome.Succeeded, outcome);
        }
    }

    /// <summary>
    /// Failures from one address do not throttle another.
    /// </summary>
    /// <remarks>
    /// The assertion that stops the count being global by accident. A throttle that
    /// accidentally partitioned on nothing would pass every other test in this class and take
    /// the whole firm offline the first time anybody in the world guessed at this form.
    /// </remarks>
    [Fact]
    public async Task Failures_from_somewhere_else_do_not_throttle_this_address()
    {
        const string noisy = "203.0.113.14";
        const string quiet = "203.0.113.15";

        await factory.CreateAccountAsync("elsewhere@jiranisokotech.co.ke", Password);

        for (var attempt = 0; attempt < SignInThrottle.MostFailures + 3; attempt++)
        {
            await AttemptAsync($"noise-{attempt}@example.com", "guessed", noisy);
        }

        Assert.Equal(
            SignInOutcome.TooManyAttempts,
            await AttemptAsync("elsewhere@jiranisokotech.co.ke", Password, noisy));

        Assert.Equal(
            SignInOutcome.Succeeded,
            await AttemptAsync("elsewhere@jiranisokotech.co.ke", Password, quiet));
    }

    /// <summary>
    /// A connection this process cannot see the address of is never throttled.
    /// </summary>
    /// <remarks>
    /// A decision rather than an oversight. A null address means the request arrived without
    /// one this process could read — which happens behind a reverse proxy that has not been
    /// configured to forward it. Throttling every such request as though they came from one
    /// caller would take the whole firm offline the first time somebody changed a proxy, and
    /// the in-memory limiter already bounds the volume of those under one partition, which is
    /// the right place for a guess.
    /// </remarks>
    [Fact]
    public async Task An_unknown_address_is_never_throttled()
    {
        await factory.CreateAccountAsync("behind-a-proxy@jiranisokotech.co.ke", Password);

        for (var attempt = 0; attempt < SignInThrottle.MostFailures + 3; attempt++)
        {
            await AttemptAsync($"unseen-{attempt}@example.com", "guessed", null);
        }

        Assert.Equal(
            SignInOutcome.Succeeded,
            await AttemptAsync("behind-a-proxy@jiranisokotech.co.ke", Password, null));
    }

    /// <summary>
    /// The window is a window: failures old enough to have expired do not count.
    /// </summary>
    /// <remarks>
    /// Without this the throttle is a permanent ban with extra steps, and the sentence the
    /// page shows — that waiting is the fix — becomes a lie somebody discovers at the worst
    /// possible moment.
    /// </remarks>
    [Fact]
    public async Task Failures_older_than_the_window_no_longer_count()
    {
        const string address = "203.0.113.16";

        await factory.CreateAccountAsync("patient@jiranisokotech.co.ke", Password);

        for (var attempt = 0; attempt < SignInThrottle.MostFailures; attempt++)
        {
            await AttemptAsync($"stale-{attempt}@example.com", "guessed", address);
        }

        Assert.Equal(
            SignInOutcome.TooManyAttempts,
            await AttemptAsync("patient@jiranisokotech.co.ke", Password, address));

        /*
         * The clock in the running application is real, so the failures are aged by moving
         * them rather than by waiting a quarter of an hour. What is under test is the
         * comparison, and the comparison cannot tell the difference.
         */
        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var rows = await database.Set<SignInRecord>()
                .Where(record => record.IpAddress == address)
                .ToListAsync();

            foreach (var row in rows)
            {
                /*
                 * Through the change tracker rather than as one UPDATE, because SQLite cannot
                 * translate date arithmetic inside ExecuteUpdate and the suite runs on SQLite.
                 * Also because At has no setter — it is written once, deliberately, and a test
                 * is not a reason to open it.
                 */
                database.Entry(row).Property(nameof(SignInRecord.At)).CurrentValue =
                    row.At - SignInThrottle.Window - TimeSpan.FromMinutes(1);
            }

            await database.SaveChangesAsync();
        });

        Assert.Equal(
            SignInOutcome.Succeeded,
            await AttemptAsync("patient@jiranisokotech.co.ke", Password, address));
    }

    private Task<SignInOutcome> AttemptAsync(string email, string password, string? address) =>
        factory.InRequestAsync(services =>
            services.GetRequiredService<SignInService>().PasswordSignInAsync(
                email, password, remember: false, ipAddress: address, userAgent: "xunit"));

    private async Task<int> RecordsFrom(string address)
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

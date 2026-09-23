using JiranisokoTech.Application.Business;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Tests.Infrastructure;
using JiranisokoTech.Web.Reporting;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Bringing a spreadsheet of clients in.
/// </summary>
/// <remarks>
/// The tests that matter are the ones proving nothing is written when anything is wrong. A
/// partial import leaves somebody comparing a spreadsheet against a screen to find out what
/// went in, and their only options are to work out the difference by hand or to import again
/// and get duplicates of everything that succeeded.
/// </remarks>
public class ClientImportTests
{
    /// <summary>
    /// One bad row stops the whole file, including the good rows before it.
    /// </summary>
    /// <remarks>
    /// The central guarantee. Asserted by counting the clients afterwards rather than by
    /// reading the outcome, because the outcome is what the code says and the count is what
    /// actually happened.
    /// </remarks>
    [Fact]
    public async Task One_bad_row_stops_the_whole_file()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var outcome = await Importer(context).RunAsync(
            """
            name,code
            Acme Haulage,acme
            ,no-name-here
            Beta Logistics,beta
            """,
            commit: true);

        Assert.Equal(ImportState.WouldFail, outcome.State);

        // And the two good rows are not in the database, though they were read first.
        await using var after = fixture.NewContext();
        Assert.Empty(await after.Clients.ToListAsync());
    }

    /// <summary>A file with nothing wrong is imported whole.</summary>
    [Fact]
    public async Task A_good_file_is_imported_whole()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await using (var context = fixture.NewContext())
        {
            var outcome = await Importer(context).RunAsync(
                """
                name,code,contact,email
                Acme Haulage,acme,Jane Mwangi,jane@acme.example
                Beta Logistics,beta,,
                """,
                commit: true);

            Assert.Equal(ImportState.Done, outcome.State);
            Assert.Equal(2, outcome.Clients.Count);
        }

        await using var after = fixture.NewContext();

        Assert.Equal(2, await after.Clients.CountAsync());
    }

    /// <summary>
    /// A dry run says what would happen and writes nothing.
    /// </summary>
    /// <remarks>
    /// Somebody importing four hundred clients wants to see what would happen before it does,
    /// and an importer with only one button would be used once and distrusted afterwards.
    /// </remarks>
    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await using (var context = fixture.NewContext())
        {
            var outcome = await Importer(context).RunAsync(
                "name,code\nAcme Haulage,acme\n", commit: false);

            Assert.Equal(ImportState.WouldWork, outcome.State);
            Assert.Single(outcome.Clients);
        }

        await using var after = fixture.NewContext();

        Assert.Empty(await after.Clients.ToListAsync());
    }

    /// <summary>
    /// The same client twice in one file is caught.
    /// </summary>
    /// <remarks>
    /// The ordinary case — somebody appended a second export to the first. Checking only
    /// against what is stored would let the first copy in and then refuse the second, which
    /// is the worst of both answers.
    /// </remarks>
    [Fact]
    public async Task The_same_client_twice_in_one_file_is_caught()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var outcome = await Importer(context).RunAsync(
            """
            name,code
            Acme Haulage,acme
            Acme Haulage,acme-two
            """,
            commit: true);

        Assert.Equal(ImportState.WouldFail, outcome.State);
        Assert.Contains("more than once", Assert.Single(outcome.Problems).What);
    }

    /// <summary>
    /// Two rows whose codes would collide after slugging are caught.
    /// </summary>
    /// <remarks>
    /// The code is derived the same way the service derives it, so this check agrees with the
    /// one that would refuse it. Comparing the raw text instead would pass "Acme Ltd" and
    /// "acme ltd" as different and fail on the second once both had become one slug — which
    /// is a partial import by another route.
    /// </remarks>
    [Fact]
    public async Task Codes_that_would_collide_after_slugging_are_caught()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var outcome = await Importer(context).RunAsync(
            """
            name,code
            First Company,Acme Ltd
            Second Company,acme ltd
            """,
            commit: true);

        Assert.Equal(ImportState.WouldFail, outcome.State);
        Assert.Contains("twice", Assert.Single(outcome.Problems).What);
    }

    /// <summary>A client already here is caught rather than duplicated.</summary>
    [Fact]
    public async Task A_client_already_here_is_caught()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await using (var context = fixture.NewContext())
        {
            await Service(context).TakeOnAsync("Acme Haulage", "acme");
        }

        await using (var context = fixture.NewContext())
        {
            var outcome = await Importer(context).RunAsync(
                "name,code\nAcme Haulage,acme\n", commit: true);

            Assert.Equal(ImportState.WouldFail, outcome.State);
            Assert.Contains("already a client", Assert.Single(outcome.Problems).What);
        }

        await using var after = fixture.NewContext();

        // Still one, not two.
        Assert.Equal(1, await after.Clients.CountAsync());
    }

    /// <summary>A file with no name column is refused before any row is read.</summary>
    [Fact]
    public async Task A_file_without_a_name_column_is_refused()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var outcome = await Importer(context).RunAsync(
            "code,email\nacme,a@b.co\n", commit: true);

        Assert.Equal(ImportState.Refused, outcome.State);
        Assert.Contains("name", outcome.Refusal);
    }

    /// <summary>
    /// A file larger than the limit is refused with the reason.
    /// </summary>
    /// <remarks>
    /// Not a technical limit: it is the point past which an import is a data migration
    /// somebody should be watching rather than a form submission waiting on a browser, and a
    /// request that runs for minutes is one a proxy cuts in the middle.
    /// </remarks>
    [Fact]
    public async Task A_file_with_too_many_rows_is_refused()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var rows = string.Join(
            "\n", Enumerable.Range(1, ClientImport.Most + 1).Select(one => $"Client {one}"));

        var outcome = await Importer(context).RunAsync($"name\n{rows}\n", commit: true);

        Assert.Equal(ImportState.Refused, outcome.State);
        Assert.Contains("Split the file", outcome.Refusal);
    }

    private static ClientImport Importer(TestDbContext context) =>
        new(Service(context), new BusinessQueries(context, new TestClock()));

    private static ClientService Service(TestDbContext context) =>
        new(new BusinessRepository(context));
}

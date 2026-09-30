using System.Net;
using System.Reflection;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Workflows;

/// <summary>
/// Every page with a fixed address opens for somebody allowed to see it.
/// </summary>
/// <remarks>
/// Read from the routes the application declares rather than from a list, so a page added
/// next month is opened here without anybody remembering to add it. Signed in as the owner,
/// who holds every permission, so a refusal is never the reason for what comes back.
///
/// Addresses with a parameter in them are left to the tests for their pages, because opening
/// one means knowing an identifier that exists; an empty database has none.
///
/// Run on both databases. On SQLite it is a cheap check that nothing throws on an empty
/// database. On PostgreSQL it is the check that would have caught the security centre, which
/// rendered against a null the moment a query genuinely awaited — see
/// <see cref="PostgresApplicationFactory"/>.
/// </remarks>
public static partial class EveryPageOpens
{
    public static async Task CheckAsync(ApplicationFactory factory)
    {
        var browser = await Browsing.SignedInAsync(factory, "every-page@jiranisokotech.co.ke", Roles.Owner);
        var failures = new List<string>();

        var unlabelled = new List<string>();

        foreach (var address in Addresses())
        {
            var response = await browser.GetAsync(address);
            var html = await response.Content.ReadAsStringAsync();

            if (response.StatusCode >= HttpStatusCode.InternalServerError
                || html.Contains("Quote this reference if you report it", StringComparison.Ordinal))
            {
                failures.Add($"{address} → {(int)response.StatusCode}");
                continue;
            }

            unlabelled.AddRange(Unlabelled(html).Select(control => $"{address}  {control}"));
        }

        Assert.True(failures.Count == 0, "These pages did not open:\n  " + string.Join("\n  ", failures));

        Assert.True(
            unlabelled.Count == 0,
            "A control with no label is announced by a screen reader as \"edit text\" or \"combo box\" "
            + "and nothing else. Wrap it in a <label>, point a label's for= at its id, or give it an "
            + "aria-label:\n  " + string.Join("\n  ", unlabelled.Distinct()));
    }

    /// <summary>
    /// Form controls a person can see that nothing names.
    /// </summary>
    /// <remarks>
    /// Section 81 lists accessibility in the definition of done, and nothing checked any of it.
    /// This is the part a machine can check without a browser: every visible input, select
    /// and text area is named by a label around it, a label pointing at it, or an aria-label.
    /// Hidden fields and buttons are left out — a button is named by its own text.
    /// </remarks>
    public static IEnumerable<string> Unlabelled(string html)
    {
        var labelledIds = LabelFor().Matches(html).Select(match => match.Groups[1].Value).ToHashSet();
        var depth = 0;

        foreach (System.Text.RegularExpressions.Match tag in Tag().Matches(html))
        {
            var text = tag.Value;
            var name = tag.Groups[2].Value.ToLowerInvariant();

            if (name == "label")
            {
                depth += tag.Groups[1].Value == "/" ? -1 : 1;
                continue;
            }

            if (tag.Groups[1].Value == "/" || name is not ("input" or "select" or "textarea"))
            {
                continue;
            }

            var type = TypeOf().Match(text) is { Success: true } typed ? typed.Groups[1].Value.ToLowerInvariant() : "text";

            if (name == "input" && type is "hidden" or "submit" or "button" or "reset" or "image")
            {
                continue;
            }

            if (depth > 0
                || text.Contains("aria-label", StringComparison.Ordinal)
                || text.Contains("aria-labelledby", StringComparison.Ordinal)
                || (IdOf().Match(text) is { Success: true } id && labelledIds.Contains(id.Groups[1].Value)))
            {
                continue;
            }

            yield return NameOf().Match(text) is { Success: true } named
                ? $"<{name} name=\"{named.Groups[1].Value}\">"
                : $"<{name}>";
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<(/?)(label|input|select|textarea)\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Tag();

    [System.Text.RegularExpressions.GeneratedRegex(@"<label\b[^>]*\bfor=""([^""]+)""")]
    private static partial System.Text.RegularExpressions.Regex LabelFor();

    [System.Text.RegularExpressions.GeneratedRegex(@"\btype=""([^""]+)""")]
    private static partial System.Text.RegularExpressions.Regex TypeOf();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bid=""([^""]+)""")]
    private static partial System.Text.RegularExpressions.Regex IdOf();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bname=""([^""]+)""")]
    private static partial System.Text.RegularExpressions.Regex NameOf();

    public static IReadOnlyList<string> Addresses() =>
        typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Where(template => !template.Contains('{'))

            // Signing out ends the session the rest of the sweep uses, and the error page is
            // an error page on purpose.
            .Where(template => template is not ("/sign-out" or "/error"))
            .Distinct()
            .OrderBy(template => template)
            .ToList();
}

public class EveryPageOpensTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    [Fact]
    public Task Every_page_opens_on_sqlite() => EveryPageOpens.CheckAsync(factory);

    /// <summary>
    /// And again with records in the database, which is a different question.
    /// </summary>
    /// <remarks>
    /// <b>This exists because the sweep above was green while /accounting was an error screen.</b>
    /// The remark on <see cref="EveryPageOpens"/> says what it proves — "on SQLite it is a cheap
    /// check that nothing throws on an empty database" — and that is honest and is exactly the
    /// hole. A page that renders an empty list fine and throws on the first row is invisible to
    /// it, and that is the commoner fault of the two: an empty page runs almost no code.
    ///
    /// The accounting report held its totals in a <c>Dictionary&lt;Guid?, long&gt;</c> with null
    /// meaning "not coded to an account". A dictionary refuses a null key whatever its key type
    /// is, so the first uncoded invoice to reach the report threw — and every invoice is uncoded
    /// until somebody codes it. With no invoices at all, nothing reached it and the page opened.
    ///
    /// <b>Deliberately a handful of records rather than the demonstration seed.</b> That seed
    /// builds eighteen months of history and drains the outbox, which is minutes; this wants the
    /// cheapest database that is not empty. One of each of the things pages read, and each chosen
    /// to be the awkward version: an invoice that is sent and coded to nothing, a project with no
    /// client, a work item assigned to nobody. A seed of tidy records would open every page and
    /// prove less than the empty one does.
    /// </remarks>
    [Fact]
    public async Task Every_page_opens_with_records_in_the_database()
    {
        await factory.InScopeAsync(async services =>
        {
            var tag = Guid.CreateVersion7().ToString("N")[^8..];

            var client = await services.GetRequiredService<ClientService>()
                .TakeOnAsync("Swept " + tag, "swept-" + tag);

            var invoices = services.GetRequiredService<InvoiceService>();
            var invoice = await invoices.DraftAsync(client.Id, "KES");

            await invoices.AddLineAsync(
                invoice.Id, "Work nobody has coded", 1, Money.Of(250_000_00L, "KES"));

            /*
             * Sent and coded to nothing. Only a sent invoice is income, and an uncoded one is
             * what the accounting report could not survive — so this single row is the whole
             * reason this test exists.
             */
            await invoices.SendAsync(invoice.Id);

            var work = services.GetRequiredService<WorkService>();
            var project = await work.BeginProjectAsync("Swept " + tag);

            var people = services.GetRequiredService<PeopleService>();
            var person = await people.HireAsync(
                "Swept " + tag, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));

            await people.StartAsync(person.Id);

            // Assigned to nobody, on purpose: a null assignee is what half the board looks like.
            await work.RaiseAsync("Something swept " + tag, person.Id, project.Id);
        });

        await EveryPageOpens.CheckAsync(factory);
    }

    /// <summary>The sweep finds pages at all, so an empty list cannot pass it.</summary>
    [Fact]
    public void The_sweep_is_not_empty() =>
        Assert.True(EveryPageOpens.Addresses().Count > 60);
}

public class EveryPageOpensOnPostgresTests
{
    [PostgresFact]
    public async Task Every_page_opens_on_postgres()
    {
        using var factory = new PostgresApplicationFactory();

        await EveryPageOpens.CheckAsync(factory);
    }
}

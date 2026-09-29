using System.Net;
using System.Reflection;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using Microsoft.AspNetCore.Components;
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
public static class EveryPageOpens
{
    public static async Task CheckAsync(ApplicationFactory factory)
    {
        var browser = await Browsing.SignedInAsync(factory, "every-page@jiranisokotech.co.ke", Roles.Owner);
        var failures = new List<string>();

        foreach (var address in Addresses())
        {
            var response = await browser.GetAsync(address);
            var html = await response.Content.ReadAsStringAsync();

            if (response.StatusCode >= HttpStatusCode.InternalServerError
                || html.Contains("Quote this reference if you report it", StringComparison.Ordinal))
            {
                failures.Add($"{address} → {(int)response.StatusCode}");
            }
        }

        Assert.True(failures.Count == 0, "These pages did not open:\n  " + string.Join("\n  ", failures));
    }

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

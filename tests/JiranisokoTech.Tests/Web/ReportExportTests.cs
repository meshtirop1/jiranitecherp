using System.Net;
using System.Text;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Web.Reporting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The reporting page as a file, and the two things a file can do that a page
/// cannot.
/// </summary>
/// <remarks>
/// A page renders its text into HTML, where the worst a stray character does is
/// disappear. A CSV is opened in a spreadsheet, which is an interpreter: a cell
/// beginning with an equals sign is executed, on the machine of whoever was
/// senior enough to be reading the firm's figures. And a file leaves the
/// application — it is forwarded, so what it contains is decided once and cannot
/// be taken back, which is why the money sections are asked about here as well
/// as on the screen.
/// </remarks>
public class ReportExportTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    private const string Address = "/reports/where-things-stand.csv";

    [Fact]
    public async Task A_stranger_is_sent_to_sign_in()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(Address);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// The export is behind the permission the page is behind.
    /// </summary>
    /// <remarks>
    /// An endpoint is not a page and does not inherit a page's attribute, so this
    /// is the check that the address is not simply open to anybody with a login.
    /// </remarks>
    [Fact]
    public async Task Somebody_who_cannot_open_the_page_cannot_have_the_file()
    {
        var developer = await SignedInAsync("csv-dev@jiranisokotech.co.ke", Roles.Developer);

        var response = await developer.GetAsync(Address);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_file_is_a_csv_named_for_the_day_it_describes()
    {
        var browser = await SignedInAsync("csv-name@jiranisokotech.co.ke", Roles.DepartmentHead);

        var response = await browser.GetAsync(Address);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var expected = StandingCsv.FileName(DateOnly.FromDateTime(DateTime.UtcNow));

        Assert.Equal(
            expected,
            response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        // Excel on Windows reads a CSV without a byte order mark in the machine's
        // ANSI code page, whatever the response says, and every name with an
        // apostrophe or an accent arrives mangled.
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
    }

    /// <summary>
    /// A name with a comma and a quote in it comes out of the file as it went in.
    /// </summary>
    /// <remarks>
    /// The comma is the one that breaks the file rather than the cell: unquoted,
    /// it ends the field, and every column to the right of it shifts by one — so
    /// the away list reads as a person whose leave started on a date that belongs
    /// to somebody else. Employees' names are typed by whoever set them up and
    /// nothing anywhere asks them to avoid punctuation.
    /// </remarks>
    [Fact]
    public async Task A_name_with_a_comma_and_a_quote_survives_the_trip()
    {
        await AwayAsync("Otieno, Grace \"Gigi\"");

        var browser = await SignedInAsync("csv-quote@jiranisokotech.co.ke", Roles.DepartmentHead);

        var csv = await FileFor(browser);

        // Wrapped, and the inner quotes doubled, which is the whole of the
        // escaping the format has.
        Assert.Contains("\"Otieno, Grace \"\"Gigi\"\"\"", csv);

        // Read back with the same rules, the field is the name again.
        var field = Fields(Line(csv, "Otieno")).ElementAt(1);

        Assert.Equal("Otieno, Grace \"Gigi\"", field);
    }

    /// <summary>
    /// A name that begins with an equals sign arrives as text, not as a formula.
    /// </summary>
    /// <remarks>
    /// Excel, LibreOffice and Google Sheets all execute it. Quoting the field
    /// does not help: the quotes are the CSV's own escaping and are gone before
    /// the cell is parsed. The leading apostrophe is what marks the cell as text,
    /// and the name is still legible with one in front of it — which stripping
    /// the character would not be.
    /// </remarks>
    [Fact]
    public async Task A_name_that_begins_with_an_equals_sign_is_not_a_formula()
    {
        await AwayAsync("=HYPERLINK(\"http://example.invalid\",\"Wekesa\")");

        var browser = await SignedInAsync("csv-formula@jiranisokotech.co.ke", Roles.DepartmentHead);

        var csv = await FileFor(browser);

        var field = Fields(Line(csv, "HYPERLINK")).ElementAt(1);

        Assert.StartsWith("'=", field);
        Assert.Contains("Wekesa", field);

        // And no cell anywhere in the file starts one.
        foreach (var line in csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.All(Fields(line), one => Assert.False(
                one.StartsWith('=') || one.StartsWith('+') || one.StartsWith('@'),
                $"A field in the export is a formula: {one}"));
        }
    }

    /// <summary>
    /// A head of department gets the money owed to their people and not the money
    /// clients owe the firm.
    /// </summary>
    /// <remarks>
    /// Exactly what the page shows them, and the reason the file is built from the
    /// same two permission questions. A head holds reporting so they can see their
    /// team's hours and absence; client debt is behind invoices.view on the screen,
    /// and an export that ignored that would hand them, in a forwardable file, the
    /// one thing the page refuses them.
    /// </remarks>
    [Fact]
    public async Task A_head_of_department_is_not_given_what_clients_owe()
    {
        var head = await SignedInAsync("csv-head@jiranisokotech.co.ke", Roles.DepartmentHead);

        var csv = await FileFor(head);

        Assert.DoesNotContain("Money owed to us", csv);
        Assert.DoesNotContain("Work done and not billed", csv);

        // What they do hold: their own people's unpaid claims, and the queues.
        Assert.Contains("Money we owe our own people", csv);
        Assert.Contains("Waiting on somebody", csv);
    }

    /// <summary>
    /// And a delivery manager, who holds the invoices and not the claims, gets the
    /// mirror image.
    /// </summary>
    [Fact]
    public async Task A_delivery_manager_is_given_the_debt_and_not_the_claims()
    {
        var manager = await SignedInAsync("csv-pm@jiranisokotech.co.ke", Roles.ProjectManager);

        var csv = await FileFor(manager);

        Assert.Contains("Money owed to us", csv);
        Assert.DoesNotContain("Money we owe our own people", csv);
    }

    /// <summary>
    /// The page offers the file, or nobody finds it.
    /// </summary>
    [Fact]
    public async Task The_page_offers_the_export()
    {
        var browser = await SignedInAsync("csv-link@jiranisokotech.co.ke", Roles.DepartmentHead);

        var html = await (await browser.GetAsync("/reports")).Content.ReadAsStringAsync();

        Assert.Contains(Address, html);
    }

    /// <summary>
    /// The escaping on its own, including the cases a name cannot reach.
    /// </summary>
    [Theory]
    [InlineData("Acme Logistics", "Acme Logistics")]
    [InlineData("Acme, Logistics", "\"Acme, Logistics\"")]
    [InlineData("Acme \"the depot\"", "\"Acme \"\"the depot\"\"\"")]
    [InlineData("Two\r\nlines", "\"Two\r\nlines\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+41 7", "'+41 7")]
    [InlineData("-Smith", "'-Smith")]
    [InlineData("@channel", "'@channel")]
    [InlineData("=1,2", "\"'=1,2\"")]
    [InlineData(null, "")]
    public void A_field_is_quoted_when_it_must_be_and_defused_when_it_must_be(
        string? value, string expected)
    {
        Assert.Equal(expected, Csv.Field(value));
    }

    /// <summary>Approved leave inside the fortnight, under a name somebody typed.</summary>
    private async Task AwayAsync(string name)
    {
        using var scope = factory.Services.CreateScope();

        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
        var leave = scope.ServiceProvider.GetRequiredService<LeaveService>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var person = await people.HireAsync(name, today);
        await people.StartAsync(person.Id);

        var asked = await leave.AskForAsync(
            person.Id, LeaveKind.Annual, today.AddDays(2), today.AddDays(4), "A break");

        await leave.SubmitAsync(asked.Id);
        await leave.RecordDecisionAsync(asked.Id, true, null);
    }

    private static async Task<string> FileFor(HttpClient browser)
    {
        var response = await browser.GetAsync(Address);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The byte order mark arrives as a character on the front of the string
        // and is not part of any field.
        return (await response.Content.ReadAsStringAsync()).TrimStart('﻿');
    }

    private static string Line(string csv, string containing) =>
        csv.Split("\r\n").Single(line => line.Contains(containing, StringComparison.Ordinal));

    /// <summary>
    /// One line read back as a browser-free spreadsheet would read it.
    /// </summary>
    /// <remarks>
    /// Deliberately not a split on commas: the whole point of the quoting is that
    /// a comma inside a field is not a separator, and a test that split on commas
    /// would pass against a file no spreadsheet could read.
    /// </remarks>
    private static IEnumerable<string> Fields(string line)
    {
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];

            if (quoted)
            {
                if (character != '"')
                {
                    field.Append(character);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                yield return field.ToString();
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        yield return field.ToString();
    }

    private async Task<HttpClient> SignedInAsync(string email, string role)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);
            }

            var stored = await users.FindByEmailAsync(email);

            if (!await users.IsInRoleAsync(stored!, role))
            {
                await users.AddToRoleAsync(stored!, role);
            }
        }

        var browser = factory.CreateBrowser();

        var form = await browser.GetAsync("/sign-in");
        var fields = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = Password,
            });

        await browser.PostAsync("/sign-in", new FormUrlEncodedContent(fields));

        return browser;
    }
}

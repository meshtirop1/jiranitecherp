using System.Net;
using System.Text;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Documents;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Who gets handed a file, over HTTP, as a real signed-in person.
/// </summary>
/// <remarks>
/// The unit tests cover what is accepted and where it is stored. This covers
/// the part that cannot be checked any other way: the endpoint reads the row,
/// works out which permission governs the thing the file is attached to, and
/// answers accordingly. Getting that wrong means a client contract handed to
/// anybody with a login, and nobody reports being given a file they should not
/// have had.
///
/// A personnel file is the one kind where the permission in that table is not
/// the answer on its own, and the cases below are most of why this file exists.
/// employees.view — the permission to read the staff roster — is held by HR,
/// every department head, every delivery manager, the administrator and the
/// owner. What people attach to a person is a contract with a salary on it, a
/// disciplinary letter, a scan of a passport. The endpoint narrows the table to
/// employees.manage or to the person the file is about, and a narrowing that
/// lives only in a comment is not one.
/// </remarks>
public class DocumentDownloadTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public async Task A_stranger_is_sent_to_sign_in()
    {
        var id = await AttachToAClientAsync();

        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync($"/documents/{id}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// Somebody who may see clients is handed the file.
    /// </summary>
    [Fact]
    public async Task A_delivery_manager_can_download_a_client_document()
    {
        var id = await AttachToAClientAsync();

        var browser = await SignedInAsync("docpm@jiranisokotech.co.ke", Roles.ProjectManager);

        var response = await browser.GetAsync($"/documents/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Always an attachment, never inline: a PDF or an SVG rendered in the
        // browser runs on this application's origin with this person's session.
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("contract.pdf", response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    /// <summary>
    /// A developer holds no client permission and is told the file does not
    /// exist.
    /// </summary>
    /// <remarks>
    /// Not forbidden. A 403 on a guessable identifier confirms that a document
    /// exists and roughly what it is attached to, which is most of what
    /// somebody probing wanted to learn.
    /// </remarks>
    [Fact]
    public async Task A_developer_is_told_a_client_document_does_not_exist()
    {
        var id = await AttachToAClientAsync();

        var browser = await SignedInAsync("docdev@jiranisokotech.co.ke", Roles.Developer);

        var response = await browser.GetAsync($"/documents/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_identifier_that_matches_nothing_is_a_not_found()
    {
        var browser = await SignedInAsync("docpm2@jiranisokotech.co.ke", Roles.ProjectManager);

        var response = await browser.GetAsync($"/documents/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// HR hold employees.manage, which is whose job a personnel file is.
    /// </summary>
    [Fact]
    public async Task Hr_can_download_a_personnel_file()
    {
        var person = await HireAsync("Winnie Cheptoo");
        var id = await AttachToAsync(person);

        var browser = await SignedInAsync("dochr@jiranisokotech.co.ke", Roles.HumanResources);

        var response = await browser.GetAsync($"/documents/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("employment-contract.pdf", response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    /// <summary>
    /// A delivery manager holds employees.view and is still told nothing.
    /// </summary>
    /// <remarks>
    /// This is the case the whole narrowing exists for. A delivery manager holds
    /// employees.view so they can see who is on the team; the permission table
    /// maps Employee to exactly that, so copying the client page's rule would
    /// have handed them the salary and the disciplinary history of everybody on
    /// it. Nobody reports having been given a file they should not have had, so
    /// this has to be a test rather than something anybody would notice.
    ///
    /// This account is also unlinked, so it covers the other half at the same
    /// time: an account with no staff record resolves to nobody, and nobody must
    /// not come out of that comparison as the person the file is about.
    /// </remarks>
    [Fact]
    public async Task A_delivery_manager_is_told_a_personnel_file_does_not_exist()
    {
        var person = await HireAsync("Hilda Atieno");
        var id = await AttachToAsync(person);

        var browser = await SignedInAsync("docpm3@jiranisokotech.co.ke", Roles.ProjectManager);

        var response = await browser.GetAsync($"/documents/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Your own contract is yours, whatever you hold.
    /// </summary>
    /// <remarks>
    /// A developer holds no People permission at all and is still handed this,
    /// because the rule is who the file is about rather than what the reader may
    /// do. Note what this test does not prove: the person page needs
    /// employees.view to open, so an engineer has no page that links to this.
    /// </remarks>
    [Fact]
    public async Task A_person_can_download_their_own_personnel_file()
    {
        var person = await HireAsync("Kiprono Bett");
        var id = await AttachToAsync(person);

        var browser = await SignedInAsync(
            "docmine@jiranisokotech.co.ke", Roles.Developer, staffRecord: person);

        var response = await browser.GetAsync($"/documents/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Being somebody's colleague is not being them.
    /// </summary>
    /// <remarks>
    /// The mirror of the test above, and the one that catches an owner comparison
    /// that is true for everybody rather than for one person: without it, "the
    /// person themselves" and "anybody with a staff record" look identical from
    /// the passing side.
    ///
    /// The colleague is a delivery manager rather than a developer on purpose. A
    /// developer would be refused for holding no People permission at all, which
    /// proves nothing about the comparison — the refusal has to be because the
    /// file is somebody else's and for no other reason.
    /// </remarks>
    [Fact]
    public async Task A_colleague_is_told_a_personnel_file_does_not_exist()
    {
        var subject = await HireAsync("Faith Nyokabi");
        var id = await AttachToAsync(subject);

        var colleague = await HireAsync("Brian Otieno");

        var browser = await SignedInAsync(
            "docother@jiranisokotech.co.ke", Roles.ProjectManager, staffRecord: colleague);

        var response = await browser.GetAsync($"/documents/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<Guid> HireAsync(string name)
    {
        using var scope = factory.Services.CreateScope();

        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

        var employee = await people.HireAsync(name, Monday, jobTitle: "Engineer");

        return employee.Id;
    }

    private async Task<Guid> AttachToAsync(Guid employeeId)
    {
        using var scope = factory.Services.CreateScope();

        var documents = scope.ServiceProvider.GetRequiredService<DocumentService>();

        await using var contents = new MemoryStream(
            Encoding.UTF8.GetBytes("the signed contract, with the salary on it"));

        var attached = await documents.AttachAsync(
            AttachedTo.Employee, employeeId, contents, "employment-contract.pdf", 41, null);

        return attached.Id;
    }

    private async Task<Guid> AttachToAClientAsync()
    {
        using var scope = factory.Services.CreateScope();

        var clients = scope.ServiceProvider.GetRequiredService<ClientService>();
        var documents = scope.ServiceProvider.GetRequiredService<DocumentService>();

        var client = await clients.TakeOnAsync($"Acme {Guid.CreateVersion7():N}");

        await using var contents = new MemoryStream(Encoding.UTF8.GetBytes("the signed contract"));

        var attached = await documents.AttachAsync(
            AttachedTo.Client, client.Id, contents, "contract.pdf", 19, null);

        return attached.Id;
    }

    /// <summary>
    /// An account in a role, optionally linked to a staff record, signed in
    /// through the real form.
    /// </summary>
    private async Task<HttpClient> SignedInAsync(
        string email, string role, Guid? staffRecord = null)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);

                if (staffRecord is { } person)
                {
                    var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
                    var created = await users.FindByEmailAsync(email);

                    await people.LinkAccountAsync(person, created!.Id);
                }
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

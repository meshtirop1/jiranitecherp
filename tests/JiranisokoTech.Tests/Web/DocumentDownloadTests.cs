using System.Net;
using System.Text;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Documents;
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
/// </remarks>
public class DocumentDownloadTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

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

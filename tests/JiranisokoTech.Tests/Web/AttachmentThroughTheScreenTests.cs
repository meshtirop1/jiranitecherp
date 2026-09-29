using System.Net;
using System.Net.Http.Headers;
using System.Text;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Support;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Domain.Support;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A file chosen on a page reaches the record it was chosen on.
/// </summary>
/// <remarks>
/// <b>The first of these exists because nobody in this firm could add their own photograph, and
/// the page blamed the file they had just chosen.</b>
///
/// <c>AttachmentRepository.OwnerExistsAsync</c> knew six of the eight kinds of thing a file can
/// hang off. <c>Photo</c> was not one of them, so it threw
/// <c>ArgumentOutOfRangeException("Unknown attachment kind.")</c>; <c>AttachAsync</c> asks it
/// before storing anything; and both photograph forms catch <c>ArgumentException</c>, which is
/// its base. So the form answered with a sentence about the kind, every time, for everybody.
///
/// The reason no test saw it is the reason this file is a page test: everything that covers
/// photographs builds the row with <c>Attachment.Of</c> directly, which never asks the question.
/// <c>AttachmentKindTests</c> now walks the enum so the next missing arm fails the build; this
/// goes through the real multipart form so that the thing a person would do is the thing that is
/// asserted.
/// </remarks>
public class AttachmentThroughTheScreenTests(ApplicationFactory factory)
    : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    /// <summary>
    /// Somebody can put their own face in the staff directory.
    /// </summary>
    /// <remarks>
    /// Asserted on the row and on the absence of the refusal, because the refusal is what was
    /// actually shipped: the form came back with "Unknown attachment kind. (Parameter 'kind')"
    /// above it, which reads as a complaint about the image.
    /// </remarks>
    [Fact]
    public async Task A_staff_photograph_chosen_on_your_own_profile_is_kept()
    {
        var (browser, me) = await SomebodyOnTheStaffAsync("portrait@jiranisokotech.co.ke");

        var posted = await UploadAsync(
            browser, "/my-profile", "photo", "photo", "face.jpg", "image/jpeg");

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var said = await posted.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Unknown attachment kind", said);

        await factory.InScopeAsync(async services =>
        {
            var stored = await services.GetRequiredService<AppDbContext>().Attachments
                .AsNoTracking()
                .SingleAsync(one => one.Kind == AttachedTo.Photo && one.OwnerId == me);

            Assert.Equal("face.jpg", stored.FileName);

            // Whoever chose it, which on your own profile is you.
            Assert.Equal(me, stored.UploadedById);
        });
    }

    /// <summary>
    /// A ticket carries the picture that explains it, and the record says who sent it.
    /// </summary>
    /// <remarks>
    /// Through the shared <c>&lt;Attached&gt;</c> component, which no test had ever posted — its
    /// only host until now was a page whose address takes a parameter, so even
    /// <c>EveryPageOpensTests</c> had never rendered it. Two things are asserted because two
    /// things were wrong with it: the kind had no arm in the repository, and the component passed
    /// no uploader at all, so every file attached through it belonged to nobody.
    /// </remarks>
    [Fact]
    public async Task A_ticket_keeps_the_picture_somebody_sent_with_it_and_who_sent_it()
    {
        var (browser, me) = await SomebodyOnTheStaffAsync("screenshot@jiranisokotech.co.ke");

        var ticket = await factory.InRequestAsync(services =>
            services.GetRequiredService<SupportService>().RaiseAsync(
                "The despatch board shows yesterday's runs",
                "It has not changed since eight this morning.",
                TicketPriority.Slowing,
                Requester.Colleague,
                me));

        var posted = await UploadAsync(
            browser,
            $"/support/{ticket.Number}",
            "attach",
            "document",
            "despatch-board.png",
            "image/png");

        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);

        await factory.InScopeAsync(async services =>
        {
            var stored = await services.GetRequiredService<AppDbContext>().Attachments
                .AsNoTracking()
                .SingleAsync(one => one.Kind == AttachedTo.Ticket && one.OwnerId == ticket.Id);

            Assert.Equal("despatch-board.png", stored.FileName);
            Assert.Equal(me, stored.UploadedById);
        });

        // And it is on the page, where the person answering the ticket will look for it.
        var again = await (await browser.GetAsync($"/support/{ticket.Number}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("despatch-board.png", again);
    }

    /// <summary>
    /// A file the store would refuse says so, rather than being accepted and lost.
    /// </summary>
    /// <remarks>
    /// The other half of the shared component, and the half that proves the refusal is the
    /// store's own rather than the missing-arm exception wearing its clothes. It says what this
    /// system takes, which somebody can act on; the exception said "Unknown attachment kind",
    /// which nobody can.
    /// </remarks>
    [Fact]
    public async Task A_file_the_store_refuses_is_refused_with_a_reason_about_the_file()
    {
        var (browser, me) = await SomebodyOnTheStaffAsync("refused@jiranisokotech.co.ke");

        var ticket = await factory.InRequestAsync(services =>
            services.GetRequiredService<SupportService>().RaiseAsync(
                "Sending you the installer",
                "It is the thing that will not run.",
                TicketPriority.Asking,
                Requester.Colleague,
                me));

        var posted = await UploadAsync(
            browser,
            $"/support/{ticket.Number}",
            "attach",
            "document",
            "installer.exe",
            "application/octet-stream");

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var said = await posted.Content.ReadAsStringAsync();

        /*
         * The refusal is the store's own and names what this system takes, which is something
         * somebody can act on. What matters is that it is NOT the other sentence: "Unknown
         * attachment kind" was what every photograph upload in the firm answered, and it reads
         * as a complaint about the file rather than as the missing switch arm it actually was.
         */
        Assert.DoesNotContain("Unknown attachment kind", said);
        Assert.Contains("not a document this system accepts", said);

        await factory.InScopeAsync(async services =>
        {
            Assert.False(await services.GetRequiredService<AppDbContext>().Attachments
                .AnyAsync(one => one.OwnerId == ticket.Id));
        });
    }

    /// <summary>
    /// Post a page's file form as a browser would, with one file on it.
    /// </summary>
    /// <remarks>
    /// Multipart, because the page is statically rendered and the file arrives with the post or
    /// not at all — a test that called the service would have passed the whole time either of
    /// these faults was live.
    /// </remarks>
    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient browser,
        string path,
        string handler,
        string field,
        string fileName,
        string contentType)
    {
        var page = await browser.GetAsync(path);

        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string> { ["_handler"] = handler });

        using var form = new MultipartFormDataContent();

        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        var contents = new ByteArrayContent(Encoding.UTF8.GetBytes("pretend this is a picture"));
        contents.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(contents, field, fileName);

        return await browser.PostAsync(path, form);
    }

    /// <remarks>
    /// The tail of the identifier, because a version 7 identifier starts with the time and two
    /// made in the same millisecond share their first characters.
    /// </remarks>
    private static string Suffix() => Guid.CreateVersion7().ToString("N")[^8..];

    /// <summary>Somebody signed in whose account is linked to a staff record.</summary>
    /// <remarks>
    /// Linked, because that is what makes the uploader a name rather than a null — which is the
    /// second of the two faults these tests cover, and one an unlinked account would hide.
    /// </remarks>
    private async Task<(HttpClient Browser, Guid Me)> SomebodyOnTheStaffAsync(string email)
    {
        var browser = await SignedInAsync(email, Roles.Administrator);

        using var scope = factory.Services.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var account = (await users.FindByEmailAsync(email))!.Id;

        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

        var me = await people.HireAsync(
            "Face " + Suffix(), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));

        await people.StartAsync(me.Id);
        await people.LinkAccountAsync(me.Id, account);

        return (browser, me.Id);
    }

    private async Task<HttpClient> SignedInAsync(string email, string role)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();

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

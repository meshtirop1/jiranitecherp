using JiranisokoTech.Application.Ai;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Workflows;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Ai;

/// <summary>A person who can sign in and has a staff record, as the AI pages need.</summary>
public sealed record Staffed(Guid Account, Guid Employee, string Email, HttpClient Browser)
{
    /// <summary>What the pages would build from this person's cookie, with the role's permissions.</summary>
    public Asker As(params string[] roles) => new(
        Account, Email, roles.SelectMany(Roles.PermissionsFor).ToHashSet(), Employee);

    /// <summary>The same, with a set of permissions chosen by the test.</summary>
    public Asker Holding(IEnumerable<string> permissions) => new(Account, Email, permissions.ToHashSet(), Employee);
}

/// <summary>Seeding for the AI tests, through the same services the pages use.</summary>
public static class AiSetup
{
    public static async Task<Staffed> PersonAsync(ApplicationFactory factory, string email, params string[] roles)
    {
        var browser = await Browsing.SignedInAsync(factory, email, roles);
        var account = Guid.Empty;
        var employee = Guid.Empty;

        await factory.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var people = services.GetRequiredService<PeopleService>();

            account = (await users.FindByEmailAsync(email))!.Id;

            var hired = await people.HireAsync(email, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));
            await people.StartAsync(hired.Id);
            await people.LinkAccountAsync(hired.Id, account);
            employee = hired.Id;
        });

        return new Staffed(account, employee, email, browser);
    }

    public static async Task<Guid> ProjectAsync(ApplicationFactory factory, string name, DateOnly? due = null)
    {
        var id = Guid.Empty;

        await factory.InScopeAsync(async services =>
        {
            var work = services.GetRequiredService<WorkService>();
            var project = await work.BeginProjectAsync(name, dueOn: due);
            await work.ActivateProjectAsync(project.Id);
            id = project.Id;
        });

        return id;
    }

    public static async Task<Guid> ItemAsync(
        ApplicationFactory factory, string title, Guid project, Guid raisedBy, Guid? assignee = null)
    {
        var id = Guid.Empty;

        await factory.InScopeAsync(async services =>
            id = (await services.GetRequiredService<WorkService>()
                .RaiseAsync(title, raisedBy, project, assignee)).Id);

        return id;
    }

    public static async Task<string> InvoiceAsync(ApplicationFactory factory, string clientName)
    {
        var number = string.Empty;

        await factory.InScopeAsync(async services =>
        {
            var client = await services.GetRequiredService<ClientService>().TakeOnAsync(clientName);
            var invoices = services.GetRequiredService<InvoiceService>();
            var invoice = await invoices.DraftAsync(client.Id);

            await invoices.AddLineAsync(invoice.Id, "Discovery workshop", 1, Money.Of(150_000, invoice.Currency));
            number = invoice.Number;
        });

        return number;
    }

    /// <summary>An open advert and one application to it, with the CV given.</summary>
    public static async Task<Guid> ApplicationAsync(
        ApplicationFactory factory, Guid raisedBy, string candidateEmail, byte[]? cv, string? cvName)
    {
        var id = Guid.Empty;

        await factory.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();

            var requisition = await recruitment.RaiseRequisitionAsync(
                "Backend engineer", null, 1, "The payments work needs another pair of hands.", raisedBy);
            await recruitment.SubmitAsync(requisition.Id);
            await recruitment.RecordDecisionAsync(requisition.Id, true, null);

            var posting = await recruitment.DraftPostingAsync(
                requisition.Id, "Backend engineer", "Build the payments platform",
                "You will need five years of C# and experience of PostgreSQL in production.",
                slug: $"backend-{Guid.NewGuid():N}"[..20]);
            await recruitment.PublishAsync(posting.Id);

            using var file = cv is null ? null : new MemoryStream(cv);

            id = (await recruitment.ApplyAsync(
                posting.Id, "Achieng Odhiambo", candidateEmail, phone: "+254 700 111 222",
                cv: file, cvFileName: cvName)).Id;
        });

        return id;
    }
}

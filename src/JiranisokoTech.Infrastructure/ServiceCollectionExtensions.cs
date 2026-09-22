using System.Reflection;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Application.Approvals;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Approvals;
using JiranisokoTech.Infrastructure.Mail;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The database, chosen by what the connection string actually is.
    /// </summary>
    /// <remarks>
    /// PostgreSQL in production; SQLite when the connection string names a file,
    /// which is what lets the application run on a laptop with nothing installed
    /// and the suite run without a container. The schema is written portably so
    /// the difference stays a deployment detail rather than a fork in the code.
    /// </remarks>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Default")
            ?? "Data Source=jiranisokotech.db";

        services.AddDbContext<AppDbContext>(options =>
        {
            if (LooksLikeSqlite(connection))
            {
                options.UseSqlite(connection);
            }
            else
            {
                options.UseNpgsql(connection);
            }
        });

        services.AddSingleton<IClock, SystemClock>();

        return services;
    }

    /// <summary>
    /// The outbox dispatcher, and the map from a stored name back to an event.
    /// </summary>
    /// <remarks>
    /// The registry is built here rather than resolved lazily, so that two
    /// events sharing a short name stop the process on the way up. Discovering
    /// that at dispatch time instead would mean the first wrong delivery is also
    /// the first anybody hears of it.
    /// </remarks>
    public static IServiceCollection AddMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] eventAssemblies)
    {
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.Section));

        var assemblies = eventAssemblies.Length > 0
            ? eventAssemblies
            : [typeof(IDomainEvent).Assembly];

        services.AddSingleton(DomainEventRegistry.Build(assemblies));

        services.AddScoped<OutboxDispatcher>();
        services.AddHostedService<OutboxProcessor>();

        return services;
    }

    /// <summary>The business modules, and the storage each one asks for.</summary>
    public static IServiceCollection AddModules(this IServiceCollection services)
    {
        services.AddScoped<IPeopleRepository, PeopleRepository>();
        services.AddScoped<PeopleService>();
        services.AddScoped<PeopleQueries>();

        services.AddScoped<UserAdministration>();
        services.AddScoped<UserDirectory>();

        services.AddScoped<IApprovalRepository, ApprovalRepository>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<ApprovalQueries>();

        services.AddScoped<IWorkRepository, WorkRepository>();
        services.AddScoped<WorkService>();
        services.AddScoped<WorkQueries>();

        // Reactions between modules. People knows nothing about work items and
        // must not; the event is what carries a departure across to the board.
        services.AddScoped<IDomainEventHandler<EmployeeLeft>, ReleaseWorkWhenSomebodyLeaves>();

        return services;
    }

    /// <summary>
    /// How mail leaves, and who gets told what.
    /// </summary>
    /// <remarks>
    /// The mailer is chosen once, at startup, from configuration. Deciding per
    /// message would mean a code path that only ever runs in production, which
    /// is the one nobody has watched work.
    /// </remarks>
    public static IServiceCollection AddMail(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MailOptions>(configuration.GetSection(MailOptions.Section));

        var transport = configuration
            .GetSection(MailOptions.Section)
            .GetValue(nameof(MailOptions.Transport), MailTransport.File);

        switch (transport)
        {
            case MailTransport.Smtp:
                services.AddScoped<IMailer, SmtpMailer>();
                break;
            case MailTransport.None:
                services.AddScoped<IMailer, NullMailer>();
                break;
            default:
                services.AddScoped<IMailer, FileMailer>();
                break;
        }

        services.AddScoped<MailRecipients>();

        services.AddScoped<IDomainEventHandler<ApprovalRequested>,
            TellTheDeciderSomethingIsWaiting>();
        services.AddScoped<IDomainEventHandler<ApprovalSettled>, TellTheAskerItWasDecided>();

        return services;
    }

    private static bool LooksLikeSqlite(string connection) =>
        connection.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
        && !connection.Contains("Host=", StringComparison.OrdinalIgnoreCase);
}

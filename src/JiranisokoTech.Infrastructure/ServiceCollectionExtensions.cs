using JiranisokoTech.Application.Accounting;
using JiranisokoTech.Application.Payroll;
using JiranisokoTech.Infrastructure.Accounting;
using System.Reflection;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Application.Approvals;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Engineering;
using JiranisokoTech.Application.Integrations;
using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Infrastructure.Integrations;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Application.Currencies;
using JiranisokoTech.Infrastructure.Currencies;
using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Approvals;
using JiranisokoTech.Infrastructure.Observability;
using JiranisokoTech.Infrastructure.Scheduling;
using JiranisokoTech.Infrastructure.Authorization;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.Mail;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Application.Api;
using JiranisokoTech.Application.Documents;
using JiranisokoTech.Infrastructure.Api;
using JiranisokoTech.Infrastructure.Audit;
using JiranisokoTech.Infrastructure.Documents;
using JiranisokoTech.Infrastructure.Reporting;
using JiranisokoTech.Infrastructure.Search;
using JiranisokoTech.Infrastructure.Settings;
using JiranisokoTech.Infrastructure.Recruitment;
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

        /*
         * Looking at the queue and putting back what gave up. The machinery screen has
         * counted abandoned rows since it was built and offered nothing to do about them.
         */
        services.AddScoped<OutboxAdministration>();

        return services;
    }

    /// <summary>The business modules, and the storage each one asks for.</summary>
    public static IServiceCollection AddModules(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CvStoreOptions>(configuration.GetSection(CvStoreOptions.Section));

        services.AddScoped<IPeopleRepository, PeopleRepository>();
        services.AddScoped<PeopleService>();

        services.AddScoped<ITeamRepository, TeamRepository>();
        services.AddScoped<TeamService>();

        services.AddScoped<IPerformanceRepository, PerformanceRepository>();
        services.AddScoped<PerformanceService>();

        services.AddScoped<PeopleQueries>();

        services.AddScoped<UserAdministration>();
        services.AddScoped<UserDirectory>();

        services.AddScoped<IApprovalRepository, ApprovalRepository>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<ApprovalQueries>();

        services.AddScoped<IAccountingRepository, AccountingRepository>();

        /*
         * Payroll. Section 22, and the last gap in the finance chain: the
         * income-and-expenditure report calls its bottom line a difference and never profit
         * because the firm's largest cost was missing from it.
         */
        services.AddScoped<IPayrollRepository, Payroll.PayrollRepository>();
        services.AddScoped<PayrollService>();
        services.AddScoped<AccountingService>();
        services.AddScoped<AccountingQueries>();

        services.AddScoped<IBusinessRepository, BusinessRepository>();
        services.AddScoped<ClientService>();
        services.AddScoped<OpportunityService>();
        services.AddScoped<ContactService>();
        services.AddScoped<ContractService>();

        /*
         * Section 17's second half. Employment contracts, vendor agreements and NDAs had no
         * home, so the only place one existed was as a file on a staff record with nothing
         * knowing when it ran out.
         */
        services.AddScoped<IAgreementRepository, AgreementRepository>();
        services.AddScoped<AgreementService>();
        services.AddScoped<TimesheetService>();
        services.AddScoped<LeaveService>();
        services.AddScoped<HolidayService>();
        services.AddScoped<ExpenseService>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<InterviewService>();
        services.AddScoped<AssessmentService>();

        services.AddScoped<IRecruitmentRepository, RecruitmentRepository>();
        services.AddScoped<RecruitmentService>();

        /*
         * Offers and the checklist that follows one. Section 8, and the step section 92's
         * hiring chain stopped at — the record ended with a status called Offered and nothing
         * anywhere saying what had been offered.
         */
        services.AddScoped<OfferService>();
        services.AddScoped<OnboardingService>();

        /*
         * The asset register. Section 15, and the one place that knows where a laptop is — the
         * joiner's checklist and the leaver's list are both views onto it.
         */
        services.AddScoped<Application.Assets.IAssetRepository, Assets.AssetRepository>();
        services.AddScoped<Application.Assets.AssetService>();

        /*
         * What the firm runs and what it runs on. Section 14, and the catalogue an incident
         * names when it says what is affected.
         */
        services.AddScoped<Application.Platform.IEstateRepository, Platform.EstateRepository>();
        services.AddScoped<Application.Platform.EstateService>();

        /*
         * Feature flags. Section 68, and the only table in this application that something
         * outside it reads on a schedule — see the public API.
         */
        services.AddScoped<Application.Platform.IFlagRepository, Platform.FlagRepository>();
        services.AddScoped<Application.Platform.FlagService>();

        /*
         * The notice centre, and the two handlers that fill it. Sections 32 and 59. Nothing
         * calls the service from a page: notices are written by event handlers, through the
         * outbox, so one cannot exist for something that then failed to save.
         */
        services.AddScoped<Application.Notices.INoticeRepository, Notices.NoticeRepository>();
        services.AddScoped<
            Application.Notices.IAnnouncementRepository, Notices.AnnouncementRepository>();
        services.AddSingleton<Application.Notices.IWhereThisLives, Notices.WhereThisLives>();
        services.AddScoped<Application.Notices.NoticeService>();
        services.AddScoped<Application.Notices.AnnouncementService>();
        services.AddScoped<IDomainEventHandler<Domain.Work.WorkItemAssigned>,
            Application.Notices.TellPeopleTheirWorkMoved>();
        services.AddScoped<IDomainEventHandler<Domain.Approvals.ApprovalSettled>,
            Application.Notices.TellSomebodyTheirRequestWasSettled>();
        services.AddScoped<RecruitmentQueries>();
        services.AddScoped<ICvStore, FileCvStore>();

        /*
         * The two halves of the join between hiring and approvals. Recruitment
         * says a requisition has been submitted; something else decides who
         * approves it, and tells recruitment what they said. Neither module
         * holds a reference to the other.
         */
        services.AddScoped<IDomainEventHandler<RequisitionSubmitted>,
            OpenTheChainWhenARequisitionIsSubmitted>();
        services.AddScoped<IDomainEventHandler<ApprovalSettled>,
            TellTheRequisitionWhatWasDecided>();

        services.AddScoped<IWorkRepository, WorkRepository>();

        services.AddScoped<IPlanningRepository, Work.PlanningRepository>();

        services.AddScoped<PlanningService>();
        services.AddScoped<WorkService>();
        services.AddScoped<WorkQueries>();
        services.AddScoped<BusinessQueries>();
        services.AddScoped<ReportingQueries>();
        services.AddScoped<ProjectMoneyQueries>();
        services.AddScoped<WaitingQueries>();
        services.AddScoped<IExchangeRateRepository, ExchangeRateRepository>();
        services.AddScoped<ExchangeRateService>();
        /*
         * How far somebody can see, when the answer is neither everything nor nothing.
         * Registered before the queries that use it, and used by every one of them rather
         * than each deciding for itself what a narrow permission means — which is how the
         * search box came to treat "the projects you are on" as "every project".
         */
        services.AddScoped<Reaches>();

        /*
         * Recurring work, and what came of each run.
         *
         * The outbox reacts to what happened; a great deal of what a firm needs is nobody
         * having done anything — a certification lapsing, a contract running out — and an
         * absence raises no event. Jobs are registered here rather than created on a
         * screen, because a screen for defining arbitrary scheduled work is a screen for
         * writing an application inside an application.
         */
        services.AddScoped<IRecurringJob, WarnAboutLapsingQualifications>();
        services.AddScoped<IRecurringJob, WarnAboutExpiringContracts>();

        /*
         * Section 14's one moving part. The register is otherwise a note to whoever is looking;
         * this is the column that acts, because a certificate expires at three on a Sunday.
         */
        services.AddScoped<IRecurringJob, WarnAboutExpiringResources>();

        // Section 17's second half: the firm's own paper, on the same ladder as a client's.
        services.AddScoped<IRecurringJob, WarnAboutExpiringAgreements>();
        services.AddScoped<IRecurringJob, PruneJobHistory>();
        services.AddScoped<IRecurringJob, PruneSignInHistory>();
        services.AddScoped<IRecurringJob, RaiseRecurringExpenses>();
        services.AddHostedService<Scheduler>();
        services.AddScoped<JobQueries>();

        /*
         * Running one job, used by the scheduler and by the button on the machinery screen.
         * Scoped rather than a method on the scheduler so that both go through the same
         * code: two paths writing job history would have drifted, and the drifted one
         * would be the one nobody tested.
         */
        services.AddScoped<JobRunner>();

        /*
         * Counters, and the listener that keeps their totals for /metrics. A singleton,
         * because the totals have to outlive every request — and the listener has to be
         * started before anything records a measurement, which is what the hosted service
         * registration buys.
         */
        services.AddSingleton<MetricsReader>();
        services.AddHostedService(provider => provider.GetRequiredService<MetricsReader>());
        services.AddScoped<SearchQueries>();
        services.AddScoped<AuditQueries>();

        services.Configure<DocumentStoreOptions>(
            configuration.GetSection(DocumentStoreOptions.Section));
        services.AddScoped<IAttachmentRepository, AttachmentRepository>();
        services.AddSingleton<IDocumentStore, FileDocumentStore>();
        services.AddScoped<DocumentService>();

        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<ApiKeyService>();

        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddScoped<SettingsService>();

        // Reactions between modules. People knows nothing about work items and
        // must not; the event is what carries a departure across to the board.
        services.AddScoped<IDomainEventHandler<EmployeeLeft>, ReleaseWorkWhenSomebodyLeaves>();

        // Somebody's account was used from a browser and address it has not been used
        // from before. Sent through the outbox, so a slow mail server cannot make
        // signing in slow — see TellSomebodyAboutANewPlace.
        services.AddScoped<IDomainEventHandler<SignedInSomewhereNew>,
            TellSomebodyAboutANewPlace>();

        /*
         * The Git integration. One adapter per provider, registered as a set so
         * that adding GitLab is one more line here and nothing else — the inbox
         * and the dispatcher pick the right one out of the collection by asking
         * each what provider it speaks for.
         */
        services.Configure<GitOptions>(configuration.GetSection(GitOptions.Section));
        services.AddSingleton<IWebhookSecrets, WebhookSecrets>();
        services.AddSingleton<IGitProvider, GitHubProvider>();
        services.AddSingleton<IGitProvider, GitLabProvider>();
        services.AddSingleton<IGitProvider, BitbucketProvider>();
        services.AddSingleton<IGitProvider, AzureDevOpsProvider>();
        services.AddScoped<IEngineeringRepository, EngineeringRepository>();
        services.AddScoped<EngineeringService>();
        services.AddScoped<EngineeringQueries>();

        /*
         * Releases. Section 67, and the one part of the engineering chain that no host reports:
         * a version number and a sentence about what is in it are both claims a person makes.
         */
        services.AddScoped<IReleaseRepository, ReleaseRepository>();
        services.AddScoped<ReleaseService>();

        /*
         * Incidents and their reviews. Sections 27 and 69, which are one workflow: the only
         * moment anybody ever starts a review is the moment an incident is resolved.
         */
        services.AddScoped<Application.Incidents.IIncidentRepository,
            Incidents.IncidentRepository>();
        services.AddScoped<Application.Incidents.IncidentService>();
        services.AddScoped<Incidents.IncidentQueries>();

        /*
         * Whether each code host can reach us at all, which no other screen can tell: a
         * missing or misspelt webhook secret refuses every delivery before a row is
         * written, so the queues read zero and the jobs read green.
         */
        services.AddScoped<HostHealthQueries>();
        services.AddScoped<WebhookInbox>();
        services.AddScoped<DeliveryDispatcher>();
        services.AddHostedService<DeliveryProcessor>();

        // Merging a branch is how an engineer says the work is done, and the
        // whole point of watching the repositories is that they do not then
        // have to say it again on a board.
        services.AddScoped<IDomainEventHandler<PullRequestMerged>,
            SubmitWorkWhenPullRequestMerges>();

        /*
         * Outgoing webhooks. The fan-out handler is generic and registered once per
         * event in OutboundEvents.Offered, so what may leave the building is a list in
         * one file plus these registrations — and adding a domain event somewhere else
         * cannot quietly begin sending it to third parties.
         */
        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();
        services.AddScoped<IIntegrationRepository, IntegrationRepository>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<IntegrationQueries>();
        services.AddScoped<IOutboundSender, HttpOutboundSender>();
        services.AddScoped<OutboundDispatcher>();
        services.AddHostedService<OutboundProcessor>();

        services.AddHttpClient(HttpOutboundSender.ClientName, client =>
        {
            /*
             * Ten seconds, and it is a deliberate figure. A receiver that needs longer
             * than that to acknowledge a notification is doing its work inside the
             * request instead of queueing it, and waiting for them would let one slow
             * endpoint set the pace of every notification behind it.
             */
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JiranisokoTech-Delivery/1.0");
        });

        services.AddScoped<IDomainEventHandler<PullRequestOpened>,
            PublishToSubscribers<PullRequestOpened>>();
        services.AddScoped<IDomainEventHandler<CommitRecorded>,
            PublishToSubscribers<CommitRecorded>>();
        services.AddScoped<IDomainEventHandler<InvoiceSent>,
            PublishToSubscribers<InvoiceSent>>();
        services.AddScoped<IDomainEventHandler<InvoiceSettled>,
            PublishToSubscribers<InvoiceSettled>>();
        services.AddScoped<IDomainEventHandler<PaymentRecorded>,
            PublishToSubscribers<PaymentRecorded>>();
        services.AddScoped<IDomainEventHandler<ClientTakenOn>,
            PublishToSubscribers<ClientTakenOn>>();
        services.AddScoped<IDomainEventHandler<ClientStatusChanged>,
            PublishToSubscribers<ClientStatusChanged>>();
        services.AddScoped<IDomainEventHandler<PostingPublished>,
            PublishToSubscribers<PostingPublished>>();
        services.AddScoped<IDomainEventHandler<ApplicationReceived>,
            PublishToSubscribers<ApplicationReceived>>();
        services.AddScoped<IDomainEventHandler<CandidateHired>,
            PublishToSubscribers<CandidateHired>>();
        services.AddScoped<IDomainEventHandler<EmployeeStarted>,
            PublishToSubscribers<EmployeeStarted>>();
        services.AddScoped<IDomainEventHandler<EmployeeLeft>,
            PublishToSubscribers<EmployeeLeft>>();

        // PullRequestMerged already has a handler above; a second one for the same
        // event is fine and expected — the outbox runs every handler registered for it.
        services.AddScoped<IDomainEventHandler<PullRequestMerged>,
            PublishToSubscribers<PullRequestMerged>>();

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

        /*
         * The letters that leave this firm. Registered alongside the internal
         * ones because they go through the same outbox and the same mailer;
         * what makes them different is who reads them, which is a matter for
         * the letters rather than the plumbing.
         */
        services.AddScoped<IDomainEventHandler<ApplicationReceived>,
            TellTheCandidateWeHaveIt>();
        services.AddScoped<IDomainEventHandler<ApplicationMoved>,
            TellTheCandidateTheAnswer>();
        services.AddScoped<IDomainEventHandler<InterviewScheduled>, InviteTheCandidate>();

        return services;
    }

    private static bool LooksLikeSqlite(string connection) =>
        connection.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
        && !connection.Contains("Host=", StringComparison.OrdinalIgnoreCase);
}

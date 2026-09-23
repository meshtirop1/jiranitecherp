using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Mail;

/// <summary>
/// Finding the mailbox behind a staff record.
/// </summary>
/// <remarks>
/// There may not be one. An employee and an account are separate things on
/// purpose, and somebody who joined last week and has not been given a login has
/// no address this system knows. That is an ordinary state, so every caller here
/// handles null rather than treating it as a fault.
/// </remarks>
public sealed class MailRecipients(AppDbContext database)
{
    public async Task<(string Address, string Name)?> ForAsync(
        Guid employeeId, CancellationToken cancellationToken = default)
    {
        var found = await database.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId && employee.AccountId != null)
            .Join(
                database.Users.AsNoTracking(),
                employee => employee.AccountId,
                account => account.Id,
                (employee, account) => new
                {
                    employee.FullName,
                    account.Email,
                    account.IsActive,
                })
            .FirstOrDefaultAsync(cancellationToken);

        // Nothing is sent to an account whose access has been withdrawn. They
        // cannot act on it, and a system that keeps emailing a leaver is one
        // somebody has to explain.
        return found is { IsActive: true, Email: not null }
            ? (found.Email, found.FullName)
            : null;
    }
}

/// <summary>
/// Tells the next person that something is waiting on them.
/// </summary>
/// <remarks>
/// Runs from the outbox, so it retries on a mail server that is down and gives
/// up after eight attempts rather than for ever. It is idempotent in the only
/// sense available for email: running twice sends the same message twice, which
/// is a duplicate somebody deletes, rather than a second decision or a second
/// account.
/// </remarks>
public sealed class TellTheDeciderSomethingIsWaiting(
    AppDbContext database,
    MailRecipients recipients,
    IMailer mailer,
    IOptions<MailOptions> options,
    ILogger<TellTheDeciderSomethingIsWaiting> logger)
    : IDomainEventHandler<ApprovalRequested>
{
    public async Task HandleAsync(
        ApprovalRequested domainEvent, CancellationToken cancellationToken = default)
    {
        var request = await database.Approvals
            .AsNoTracking()
            .Include(one => one.Steps)
            .FirstOrDefaultAsync(one => one.Id == domainEvent.ApprovalId, cancellationToken);

        // Gone, or already settled by the time the dispatcher got here. Telling
        // somebody about a decision that no longer needs making is worse than
        // not telling them.
        if (request is not { Status: ApprovalStatus.Pending } || request.WaitingOn is not { } next)
        {
            return;
        }

        var decider = await recipients.ForAsync(next, cancellationToken);

        if (decider is not { } to)
        {
            /*
             * No mailbox, and nothing retrying will fix. Logged and let go:
             * throwing would put this message through eight attempts and then
             * abandon it, filling the log with the same line each time and
             * burying the failures that could have been recovered.
             */
            logger.LogInformation(
                "Nobody to email about approval {ApprovalId}: the decider has no active account.",
                domainEvent.ApprovalId);

            return;
        }

        var asker = await recipients.ForAsync(request.RequestedById, cancellationToken);

        await mailer.SendAsync(
            Letters.AwaitingDecision(
                to.Address,
                to.Name,
                Describe(request.Action),
                asker?.Name ?? "Somebody",
                Link(options.Value)),
            cancellationToken);
    }

    internal static string Describe(string action) =>
        action.Replace('.', ' ').Replace('_', ' ');

    internal static string Link(MailOptions options) =>
        $"{options.BaseAddress.TrimEnd('/')}/approvals";
}

/// <summary>
/// Tells whoever asked how it ended.
/// </summary>
/// <remarks>
/// Including when it was refused, and with the reason. A request that simply
/// stops being mentioned is one the asker chases for a fortnight.
/// </remarks>
public sealed class TellTheAskerItWasDecided(
    MailRecipients recipients,
    IMailer mailer,
    IOptions<MailOptions> options,
    ILogger<TellTheAskerItWasDecided> logger)
    : IDomainEventHandler<ApprovalSettled>
{
    public async Task HandleAsync(
        ApprovalSettled domainEvent, CancellationToken cancellationToken = default)
    {
        // Nobody needs telling that they withdrew their own request.
        if (domainEvent.Status == ApprovalStatus.Withdrawn)
        {
            return;
        }

        var asker = await recipients.ForAsync(domainEvent.RequestedById, cancellationToken);

        if (asker is not { } to)
        {
            logger.LogInformation(
                "Nobody to email about approval {ApprovalId}: the asker has no active account.",
                domainEvent.ApprovalId);

            return;
        }

        await mailer.SendAsync(
            Letters.Decided(
                to.Address,
                to.Name,
                TellTheDeciderSomethingIsWaiting.Describe(domainEvent.Action),
                domainEvent.Status == ApprovalStatus.Approved,
                domainEvent.Outcome,
                TellTheDeciderSomethingIsWaiting.Link(options.Value)),
            cancellationToken);
    }
}

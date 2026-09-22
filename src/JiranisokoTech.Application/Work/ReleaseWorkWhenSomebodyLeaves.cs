using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.People;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Application.Work;

/// <summary>
/// When somebody leaves, their open work stops being theirs.
/// </summary>
/// <remarks>
/// This is the first place the outbox earns what it costs. People and Work know
/// nothing about each other — People has no reference to a work item and could
/// not acquire one without tying the two modules together forever — and yet
/// somebody leaving plainly has to change what is on the board.
///
/// The event carries that across. Recording a departure commits one transaction;
/// the release happens in another, after it, driven by the dispatcher. The two
/// are therefore not atomic, and that is the right trade here: a moment in which
/// a leaver still appears to own three items is harmless, while a release that
/// could roll back the departure is not.
///
/// It is idempotent, as every handler must be. Running it twice releases nothing
/// the second time, because there is nothing left assigned to that person.
/// </remarks>
public sealed class ReleaseWorkWhenSomebodyLeaves(
    WorkService work,
    ILogger<ReleaseWorkWhenSomebodyLeaves> logger)
    : IDomainEventHandler<EmployeeLeft>
{
    public async Task HandleAsync(
        EmployeeLeft domainEvent, CancellationToken cancellationToken = default)
    {
        var released = await work.ReleaseWorkOfAsync(domainEvent.EmployeeId, cancellationToken);

        if (released > 0)
        {
            // Worth a line: it is a change nobody asked for directly, and the
            // items now show as owned by nobody, which somebody has to notice.
            logger.LogInformation(
                "{Name} has left; {Count} open item(s) released and now belong to nobody.",
                domainEvent.FullName,
                released);
        }
    }
}

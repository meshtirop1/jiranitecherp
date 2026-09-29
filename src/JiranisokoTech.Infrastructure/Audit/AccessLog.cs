using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Privacy;
using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Infrastructure.Audit;

/// <summary>
/// <see cref="IAccessLog"/>, written to the audit trail.
/// </summary>
/// <remarks>
/// In a scope of its own. The page asking may have its own changes tracked on its context, and a
/// read that saved them as a side effect would be a far worse fault than a read that went
/// unrecorded; and a component rendering beside the page must not share its context at all —
/// see the layout entry in CLAUDE.md.
/// </remarks>
public sealed class AccessLog(IServiceScopeFactory scopes, ICurrentUser who, IClock clock) : IAccessLog
{
    public async Task ViewedAsync(
        string action, string subjectType, Guid subjectId, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        database.AuditEntries.Add(AuditEntry.Record(
            action, subjectType, subjectId, clock.Now, who.Id, who.Name));

        await database.SaveChangesAsync(cancellationToken);
    }
}

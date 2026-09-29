using JiranisokoTech.Application.Automation;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Automation;

public sealed class AutomationRepository(AppDbContext database) : IAutomationRepository
{
    public Task<AutomationRule?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.AutomationRules.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<bool> TemplateExistsAsync(string key, CancellationToken cancellationToken = default) =>
        database.AutomationRules.AnyAsync(one => one.TemplateKey == key, cancellationToken);

    public Task<bool> HasRunAsync(Guid ruleId, CancellationToken cancellationToken = default) =>
        database.AutomationRuns.AnyAsync(one => one.RuleId == ruleId, cancellationToken);

    public Task<bool> PersonExistsAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.Employees.AnyAsync(one => one.Id == employeeId, cancellationToken);

    public Task<bool> ProjectExistsAsync(
        Guid projectId, CancellationToken cancellationToken = default) =>
        database.Projects.AnyAsync(one => one.Id == projectId, cancellationToken);

    public Task<bool> SubscriptionExistsAsync(
        Guid subscriptionId, CancellationToken cancellationToken = default) =>
        database.Subscriptions.AnyAsync(one => one.Id == subscriptionId, cancellationToken);

    public void Add(AutomationRule rule) => database.AutomationRules.Add(rule);

    public void Remove(AutomationRule rule) => database.AutomationRules.Remove(rule);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}

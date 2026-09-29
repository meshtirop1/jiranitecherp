using JiranisokoTech.Domain.Automation;

namespace JiranisokoTech.Application.Automation;

/// <summary>What the automation engine needs stored and looked up.</summary>
public interface IAutomationRepository
{
    Task<AutomationRule?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> TemplateExistsAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> HasRunAsync(Guid ruleId, CancellationToken cancellationToken = default);

    Task<bool> PersonExistsAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<bool> SubscriptionExistsAsync(
        Guid subscriptionId, CancellationToken cancellationToken = default);

    void Add(AutomationRule rule);

    void Remove(AutomationRule rule);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

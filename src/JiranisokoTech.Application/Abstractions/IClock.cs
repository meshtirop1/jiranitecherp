namespace JiranisokoTech.Application.Abstractions;

/// <summary>
/// The current time, as a dependency.
/// </summary>
/// <remarks>
/// Calling DateTimeOffset.UtcNow inside a rule makes that rule untestable at any
/// date but today — and the rules here are about probation ending, invoices
/// falling overdue and certificates expiring, all of which are only interesting
/// on some other day.
/// </remarks>
public interface IClock
{
    DateTimeOffset Now { get; }

    DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);
}

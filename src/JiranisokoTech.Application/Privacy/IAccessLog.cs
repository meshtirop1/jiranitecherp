namespace JiranisokoTech.Application.Privacy;

/// <summary>
/// Records that somebody looked at something sensitive.
/// </summary>
/// <remarks>
/// Section 55 asks for access logging. The audit trail records every change, and until this it
/// recorded no reads at all — so the one question a data protection complaint most often turns
/// on, "who looked at my salary, who opened my CV", had no answer anywhere. Only reads of what
/// is genuinely sensitive are recorded: a person's pay and identity numbers, a CV, a personnel
/// document, a subject access export. Recording every page view would bury these in noise.
/// </remarks>
public interface IAccessLog
{
    /// <param name="action">Dotted and past tense, like the rest of the trail: <c>employee.pay_viewed</c>.</param>
    Task ViewedAsync(
        string action, string subjectType, Guid subjectId, CancellationToken cancellationToken = default);
}

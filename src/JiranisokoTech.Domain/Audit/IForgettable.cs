namespace JiranisokoTech.Domain.Audit;

/// <summary>
/// A record whose personal details can be erased, and which says when they were.
/// </summary>
/// <remarks>
/// <b>The audit trail would otherwise undo every erasure.</b> Capturing a change copies the
/// values it replaced, so erasing an applicant's name and email through an ordinary save would
/// write the name and email into the trail — an append-only table read by more people than could
/// see the applicant — and the erasure would have moved the data rather than removed it. A save
/// in which <see cref="ForgottenAt"/> goes from empty to set is recorded as
/// <c>{type}.forgotten</c>, naming the fields that changed and keeping none of their values.
/// </remarks>
public interface IForgettable
{
    DateTimeOffset? ForgottenAt { get; }
}

using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Recruitment;

public enum PostingStatus
{
    Draft = 1,
    Published = 2,
    Closed = 3,
}

/// <summary>
/// The advert. What the outside world sees of a requisition.
/// </summary>
/// <remarks>
/// Separate from the requisition on purpose. A requisition is an internal
/// argument for spending money — headcount, justification, who approved it —
/// and none of that belongs on a page a stranger reads. Keeping them apart
/// means the advert can be rewritten, taken down and put back up without
/// touching the decision that authorised it.
/// </remarks>
public sealed class JobPosting : Entity, IAuditable
{
    private JobPosting()
    {
        Title = string.Empty;
        Slug = string.Empty;
        Summary = string.Empty;
        Description = string.Empty;
    }

    private JobPosting(
        Guid requisitionId,
        string title,
        Slug slug,
        string summary,
        string description,
        string? location)
    {
        RequisitionId = requisitionId;
        Title = Require(title, nameof(title));
        Slug = slug.Value;
        Summary = Require(summary, nameof(summary));
        Description = Require(description, nameof(description));
        Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        Status = PostingStatus.Draft;
    }

    public static JobPosting Draft(
        Guid requisitionId,
        string title,
        string summary,
        string description,
        string? location = null,
        string? slug = null) =>
        new(requisitionId, title, Common.Slug.From(slug ?? title), summary, description, location);

    public Guid RequisitionId { get; private init; }

    public string Title { get; private set; }

    /// <summary>The address this advert answers at, fixed once it exists.</summary>
    /// <remarks>
    /// Never re-derived from a rename. A job advert gets linked to from other
    /// sites, and a link that stops working takes the applications with it.
    /// </remarks>
    public string Slug { get; private init; }

    /// <summary>One or two sentences, for a list of openings.</summary>
    public string Summary { get; private set; }

    public string Description { get; private set; }

    public string? Location { get; private set; }

    public PostingStatus Status { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>When it stops accepting applications, if a date was set.</summary>
    public DateOnly? ClosesOn { get; private set; }

    public bool IsOpen => Status == PostingStatus.Published;

    /// <summary>
    /// Put it up.
    /// </summary>
    /// <remarks>
    /// Whether the requisition behind it is approved is not knowable from here
    /// and is checked by the service. That check is the one that matters: an
    /// advert for a post nobody agreed to pay for is a promise the firm has not
    /// made.
    /// </remarks>
    public void Publish(DateTimeOffset at)
    {
        if (Status == PostingStatus.Published)
        {
            return;
        }

        Status = PostingStatus.Published;
        PublishedAt = at;

        Raise(new PostingPublished(Id, RequisitionId, Title, Slug, at));
    }

    /// <summary>
    /// Take it down.
    /// </summary>
    /// <remarks>
    /// Applications already received are untouched. Closing an advert stops new
    /// ones arriving; it does not end anybody's candidacy, and a system that
    /// treated it that way would quietly drop people mid-process.
    /// </remarks>
    public void Close(DateTimeOffset at)
    {
        if (Status == PostingStatus.Closed)
        {
            return;
        }

        Status = PostingStatus.Closed;

        Raise(new PostingClosed(Id, RequisitionId, Title, at));
    }

    public void Rewrite(string title, string summary, string description, string? location)
    {
        Title = Require(title, nameof(title));
        Summary = Require(summary, nameof(summary));
        Description = Require(description, nameof(description));
        Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
    }

    public void ClosesBy(DateOnly? on) => ClosesOn = on;

    /// <summary>Nothing here is a secret. It is an advert.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

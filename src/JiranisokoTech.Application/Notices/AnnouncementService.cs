using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Notices;

namespace JiranisokoTech.Application.Notices;

/// <summary>What the notice board needs read and written.</summary>
public interface IAnnouncementRepository
{
    Task<Announcement?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Everything, newest first, for whoever keeps the board.</summary>
    Task<List<Announcement>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// What is on the board for one person today.
    /// </summary>
    /// <remarks>
    /// Narrowed by their department, because an announcement addressed to Delivery is noise on
    /// everybody else's board — and a board that is mostly noise stops being read, which defeats
    /// the only thing it does.
    /// </remarks>
    Task<List<Announcement>> UpForAsync(
        Guid? departmentId, DateOnly today, CancellationToken cancellationToken = default);

    /// <summary>
    /// What is up, asks to be acknowledged, and has not been by this person.
    /// </summary>
    /// <remarks>
    /// The one count worth putting in front of somebody. It is computable from the
    /// acknowledgements that exist, which is why this system records those and does not invent
    /// a read receipt to count instead.
    /// </remarks>
    Task<List<Announcement>> OutstandingForAsync(
        Guid employeeId,
        Guid? departmentId,
        DateOnly today,
        CancellationToken cancellationToken = default);

    void Add(Announcement announcement);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The firm's notice board.
/// </summary>
/// <remarks>
/// Section 6. One row read by many, which is the opposite shape from a <see cref="Notice"/> and
/// the reason this is not built on one — see the remarks on <see cref="Announcement"/>.
///
/// Unlike the notice centre, this <i>is</i> called from a page. An announcement is somebody
/// deciding to say something, not the system reporting that something happened, so there is no
/// domain event upstream of it to hang a handler on.
/// </remarks>
public sealed class AnnouncementService(IAnnouncementRepository announcements, IClock clock)
{
    public async Task<Announcement> WriteAsync(
        string title,
        string body,
        Guid byEmployeeId,
        Guid? departmentId = null,
        bool needsAcknowledgement = false,
        CancellationToken cancellationToken = default)
    {
        var announcement = Announcement.Write(
            title, body, byEmployeeId, clock.Now, departmentId, needsAcknowledgement);

        announcements.Add(announcement);
        await announcements.SaveAsync(cancellationToken);

        return announcement;
    }

    public async Task SayAsync(
        Guid id,
        string title,
        string body,
        Guid? departmentId,
        DateOnly? expiresOn,
        bool? needsAcknowledgement = null,
        CancellationToken cancellationToken = default)
    {
        var announcement = await Required(id, cancellationToken);

        announcement.Say(title, body);
        announcement.For(departmentId);
        announcement.Until(expiresOn);

        /*
         * Left alone when the caller does not pass it, so that editing a posted announcement's
         * typo does not have to restate a setting it is not allowed to change — the domain would
         * refuse, and the refusal would be about the wrong thing.
         */
        if (needsAcknowledgement is { } asks)
        {
            announcement.Asks(asks);
        }

        await announcements.SaveAsync(cancellationToken);
    }

    public async Task PostAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var announcement = await Required(id, cancellationToken);

        announcement.Post(clock.Now);

        await announcements.SaveAsync(cancellationToken);
    }

    public async Task WithdrawAsync(
        Guid id, string why, CancellationToken cancellationToken = default)
    {
        var announcement = await Required(id, cancellationToken);

        announcement.Withdraw(why, clock.Now);

        await announcements.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Somebody says they have read it.
    /// </summary>
    /// <remarks>
    /// The employee is passed in rather than read from anywhere here, because the only honest
    /// source for "who is acknowledging this" is whoever is signed in — and the application
    /// layer cannot see a request. A page that guessed would be a page where one person can
    /// acknowledge on another's behalf.
    /// </remarks>
    public async Task AcknowledgeAsync(
        Guid id, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var announcement = await Required(id, cancellationToken);

        announcement.Acknowledge(employeeId, clock.Now);

        await announcements.SaveAsync(cancellationToken);
    }

    public Task<List<Announcement>> AllAsync(CancellationToken cancellationToken = default) =>
        announcements.AllAsync(cancellationToken);

    public Task<Announcement?> OneAsync(Guid id, CancellationToken cancellationToken = default) =>
        announcements.FindAsync(id, cancellationToken);

    public Task<List<Announcement>> UpForAsync(
        Guid? departmentId, CancellationToken cancellationToken = default) =>
        announcements.UpForAsync(departmentId, clock.Today, cancellationToken);

    public Task<List<Announcement>> OutstandingForAsync(
        Guid employeeId, Guid? departmentId, CancellationToken cancellationToken = default) =>
        announcements.OutstandingForAsync(
            employeeId, departmentId, clock.Today, cancellationToken);

    private async Task<Announcement> Required(Guid id, CancellationToken cancellationToken) =>
        await announcements.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That announcement is not on file.");
}

using System.Diagnostics;
using System.Diagnostics.Metrics;

// In the application layer rather than the infrastructure, because the layers below it
// need to record measurements and cannot reference upwards. Nothing here is an
// infrastructure concern: System.Diagnostics.Metrics is part of the framework, and an
// instrument is a definition rather than a mechanism. What reads these — the listener and
// the endpoint — stays in the infrastructure where it belongs.
namespace JiranisokoTech.Application.Observability;

/// <summary>
/// The instruments this system reports about itself.
/// </summary>
/// <remarks>
/// Section 45 had structured logging and nothing countable. Logs answer "what happened
/// to this one request" and cannot answer "is the outbox draining", "how many deliveries
/// gave up today" or "is anything slower than it was last week" — and those are the
/// questions somebody asks at nine on a Monday when a client says an invoice never
/// arrived.
///
/// <b>Built on System.Diagnostics.Metrics rather than on an OpenTelemetry package set.</b>
/// The instruments here are the standard .NET ones, so an OpenTelemetry exporter can be
/// added later and will find them without anything in this file changing. What is not
/// added now is the exporter's dependency tree — half a dozen packages and a collector to
/// run — for a firm that has one server and wants a page it can look at. The
/// counters are exposed at /metrics in the format Prometheus scrapes, which is also what
/// every other collector understands.
///
/// <b>Counters count things that happened, never things that are.</b> A queue's depth is
/// not a counter, it is a question for the database, and a counter that tried to track it
/// would drift the first time a process restarted mid-batch. The monitoring screen asks
/// the database for depths and reads these for rates.
/// </remarks>
public static class Telemetry
{
    public const string Name = "JiranisokoTech.Delivery";

    /// <summary>
    /// The meter everything is registered on.
    /// </summary>
    /// <remarks>
    /// One, named for the application rather than per feature. A collector groups by meter
    /// and a dozen of them makes a dashboard that has to be assembled before it can be
    /// read.
    /// </remarks>
    public static Meter Meter { get; } = new(Name, "1.0.0");

    /// <summary>
    /// The source spans are started from.
    /// </summary>
    /// <remarks>
    /// ASP.NET already traces requests. What it cannot see is the work that happens
    /// afterwards on a background loop — dispatching the outbox, handling a webhook,
    /// posting a notification — and that is precisely the work nobody is watching when it
    /// goes wrong. These spans have no parent from a request, which is the point: they are
    /// the part of the system with nobody waiting on it.
    /// </remarks>
    public static ActivitySource Source { get; } = new(Name, "1.0.0");

    // --- the outbox ---------------------------------------------------------

    public static Counter<long> OutboxDispatched { get; } = Meter.CreateCounter<long>(
        "jiranisoko.outbox.dispatched",
        unit: "messages",
        description: "Domain events handed to their handlers.");

    public static Counter<long> OutboxFailed { get; } = Meter.CreateCounter<long>(
        "jiranisoko.outbox.failed",
        unit: "messages",
        description: "Handler attempts that threw. Retried unless abandoned.");

    /// <summary>
    /// Messages given up on.
    /// </summary>
    /// <remarks>
    /// Its own counter rather than a label on failures, because the two mean different
    /// things to whoever is woken up. A failure is the system working as designed; an
    /// abandonment is something that will never happen unless a person intervenes.
    /// </remarks>
    public static Counter<long> OutboxAbandoned { get; } = Meter.CreateCounter<long>(
        "jiranisoko.outbox.abandoned",
        unit: "messages",
        description: "Domain events that gave up and are waiting for somebody.");

    // --- webhooks in --------------------------------------------------------

    public static Counter<long> DeliveriesReceived { get; } = Meter.CreateCounter<long>(
        "jiranisoko.webhooks.received",
        unit: "deliveries",
        description: "Deliveries accepted from a Git host.");

    /// <summary>
    /// Requests refused at the door.
    /// </summary>
    /// <remarks>
    /// Worth counting separately from anything else here, because this endpoint is on the
    /// public internet and a sudden rise in refusals is somebody probing it rather than a
    /// fault. Without the count, that shows up as nothing at all.
    /// </remarks>
    public static Counter<long> DeliveriesRefused { get; } = Meter.CreateCounter<long>(
        "jiranisoko.webhooks.refused",
        unit: "requests",
        description: "Requests to a webhook endpoint that were not signed with our secret.");

    public static Counter<long> DeliveriesDeadLettered { get; } = Meter.CreateCounter<long>(
        "jiranisoko.webhooks.dead_lettered",
        unit: "deliveries",
        description: "Deliveries that could not be handled and gave up.");

    // --- webhooks out -------------------------------------------------------

    public static Counter<long> NotificationsSent { get; } = Meter.CreateCounter<long>(
        "jiranisoko.notifications.sent",
        unit: "notifications",
        description: "Notifications accepted by a subscriber's endpoint.");

    public static Counter<long> NotificationsFailed { get; } = Meter.CreateCounter<long>(
        "jiranisoko.notifications.failed",
        unit: "notifications",
        description: "Notifications a subscriber's endpoint did not accept.");

    // --- the scheduler ------------------------------------------------------

    public static Counter<long> JobsRun { get; } = Meter.CreateCounter<long>(
        "jiranisoko.jobs.run",
        unit: "runs",
        description: "Scheduled jobs that ran to completion.");

    public static Counter<long> JobsFailed { get; } = Meter.CreateCounter<long>(
        "jiranisoko.jobs.failed",
        unit: "runs",
        description: "Scheduled jobs that threw.");

    /// <summary>
    /// How long a scheduled job took.
    /// </summary>
    /// <remarks>
    /// A histogram rather than an average, for the same reason the engineering report uses
    /// a median: one nightly run that took an hour because the database was being backed
    /// up drags an average into uselessness, and the question is what a run usually costs.
    /// </remarks>
    public static Histogram<double> JobDuration { get; } = Meter.CreateHistogram<double>(
        "jiranisoko.jobs.duration",
        unit: "s",
        description: "How long each scheduled job took.");

    // --- mail ---------------------------------------------------------------

    public static Counter<long> MailSent { get; } = Meter.CreateCounter<long>(
        "jiranisoko.mail.sent",
        unit: "messages",
        description: "Messages handed to the mail transport.");

    // --- sign-in ------------------------------------------------------------

    /// <summary>
    /// Sign-in attempts, labelled by outcome.
    /// </summary>
    /// <remarks>
    /// One counter with a label rather than one per outcome, because the useful reading is
    /// the ratio between them. A rise in refusals against a flat rate of successes is an
    /// attack; a rise in both is a busy morning.
    /// </remarks>
    public static Counter<long> SignIns { get; } = Meter.CreateCounter<long>(
        "jiranisoko.signins",
        unit: "attempts",
        description: "Sign-in attempts, by what came of them.");
}

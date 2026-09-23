namespace JiranisokoTech.Application.Abstractions;

/// <summary>
/// Something that has to happen every so often, whether or not anybody asks.
/// </summary>
/// <remarks>
/// Section 42 had the outbox dispatcher running as a hosted service and nothing else. The
/// outbox is not a scheduler: it reacts to what happened, and a great deal of what a firm
/// needs is nobody having done anything. A certification lapsing, a contract running out,
/// a departure nobody finished — each of those is an absence, and an absence raises no
/// event.
///
/// <b>Jobs are registered in code, not created by users.</b> A screen for defining
/// arbitrary scheduled work is a screen for writing an application inside an application,
/// and the first thing somebody builds with it is a job that deletes rows. What is
/// offered instead is a list of the jobs this system has, when each last ran, and what
/// came of it — which is what somebody actually needs at nine on a Monday.
///
/// <b>Every job must be safe to run twice.</b> The scheduler makes no promise of exactly
/// once: a process restarting between doing the work and recording that it did leaves a
/// job that runs again, and the only defence is a job that does not mind. In practice
/// every job here is a sweep that looks for a condition and acts on what it finds, so
/// running twice finds nothing the second time.
/// </remarks>
public interface IRecurringJob
{
    /// <summary>A stable name, used as the key in the run history.</summary>
    /// <remarks>
    /// Stable because renaming it starts the history again, and the history is how
    /// somebody notices a job that has quietly stopped running.
    /// </remarks>
    string Name { get; }

    /// <summary>What it is for, shown on the monitoring screen.</summary>
    string Description { get; }

    /// <summary>
    /// How often it should run.
    /// </summary>
    /// <remarks>
    /// An interval rather than a cron expression, deliberately. Cron buys the ability to
    /// say "at two in the morning on the first of the month" and costs a parser, a
    /// timezone argument nobody wins, and a syntax that is got wrong silently. Everything
    /// this system needs is "about once a day" or "about every hour", and an interval says
    /// that without ambiguity.
    /// </remarks>
    TimeSpan Every { get; }

    /// <summary>Do the work. Returns a sentence for the run history.</summary>
    Task<string> RunAsync(CancellationToken cancellationToken = default);
}

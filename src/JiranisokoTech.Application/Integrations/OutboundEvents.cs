using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Recruitment;

namespace JiranisokoTech.Application.Integrations;

/// <summary>
/// The events an outside system may subscribe to.
/// </summary>
/// <remarks>
/// A list rather than "whatever this system happens to raise", and the difference is
/// the whole of the design here.
///
/// Every domain event is a statement about the firm's internals. Publishing all of
/// them outwards would mean that adding an event — an ordinary thing to do while
/// building a feature — silently began sending new data to every third party that
/// had ever subscribed. Nobody would have decided that, and nobody would know it had
/// happened.
///
/// So each one on this list was chosen, and the test <c>EveryOfferedEventExists</c>
/// keeps the list honest: a name here that no longer matches a real event is a
/// subscription that will never fire, which looks exactly like a working integration
/// until somebody checks.
///
/// What is deliberately absent is as important as what is here. Nothing about pay,
/// nothing about leave, nothing about expenses, nothing about an interview
/// scorecard. Those are the firm's business with its own people, and there is no
/// integration worth building that needs them.
/// </remarks>
public static class OutboundEvents
{
    /// <summary>What may be subscribed to, and what each one is for.</summary>
    /// <remarks>
    /// The description is shown beside the checkbox. Somebody choosing events is
    /// deciding what leaves the building, and an event name alone is not enough to
    /// decide that on.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Offered { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Delivery. The obvious ones for anything watching whether work shipped.
            [nameof(PullRequestMerged)] =
                "A pull request merged, with the work item it belongs to.",
            [nameof(PullRequestOpened)] = "A pull request was opened.",
            [nameof(CommitRecorded)] = "A commit was recorded against a repository.",

            // Money out. What a bookkeeping or client portal integration exists for.
            [nameof(Domain.Money.InvoiceSent)] = "An invoice was sent to a client.",
            [nameof(Domain.Money.InvoiceSettled)] = "An invoice was paid in full.",
            [nameof(Domain.Money.PaymentRecorded)] = "A payment was recorded against an invoice.",

            // Clients.
            [nameof(ClientTakenOn)] = "A new client was taken on.",
            [nameof(ClientStatusChanged)] = "A client's standing changed.",

            // Hiring, which is the one people genuinely want posted into a channel.
            [nameof(PostingPublished)] = "A job advert was published.",
            [nameof(ApplicationReceived)] = "Somebody applied for a job.",
            [nameof(CandidateHired)] = "A candidate was hired.",

            /*
             * Arrivals and departures, and nothing else about a person. A directory
             * or an access-provisioning integration needs to know that somebody
             * joined or left; it does not need their salary, their address or their
             * sick leave, and this list is where that line is drawn.
             */
            [nameof(EmployeeStarted)] = "Somebody started work.",
            [nameof(EmployeeLeft)] = "Somebody left.",
        };

    public static bool IsOffered(string eventName) => Offered.ContainsKey(eventName);
}

using System.Security.Cryptography;
using System.Text;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Api;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Recruitment;

namespace JiranisokoTech.Application.Recruitment;

using Money = JiranisokoTech.Domain.Common.Money;

/// <summary>An offer, with the secret that was just minted for it.</summary>
/// <remarks>
/// The link is returned once and never again, exactly as an API key is: what is stored is a
/// hash, so nothing in the database would let somebody accept on a candidate's behalf. If the
/// email fails to send, the offer is withdrawn and written again rather than the link being
/// recovered — there is nothing to recover it from.
/// </remarks>
public sealed record WrittenOffer(Offer Offer, string Secret);

/// <summary>
/// Offers: writing them, sending them, and what the candidate said.
/// </summary>
/// <remarks>
/// Section 8, and the step section 92's chain stopped at. The requisition, the advert, the
/// application, the interviews and the assessment all existed, and then the record ended with a
/// status called Offered and nothing anywhere saying what had been offered.
///
/// <b>Accepting creates nobody.</b> There is a separate method for that, pressed by a person,
/// and it is the same reasoning as an opportunity being won creating no client and no project:
/// separate decisions with separate consequences. A start date that moves by a fortnight
/// between acceptance and arrival is the ordinary case, and a staff record created the moment
/// somebody clicked would carry the wrong one.
/// </remarks>
public sealed class OfferService(
    IRecruitmentRepository recruitment, IPeopleRepository people, IClock clock)
{
    /// <summary>
    /// Write one. Nobody outside the firm sees it yet.
    /// </summary>
    /// <remarks>
    /// Refused if there is already a live offer against this application, because two offers
    /// with two links and two salaries is a situation nobody can resolve afterwards — whichever
    /// the candidate accepted is the one they will say they accepted.
    /// </remarks>
    public async Task<WrittenOffer> WriteAsync(
        Guid applicationId,
        string jobTitle,
        Money salary,
        PayFrequency frequency,
        DateOnly startsOn,
        DateOnly closesOn,
        string terms,
        Guid madeById,
        CancellationToken cancellationToken = default)
    {
        _ = await RequiredApplication(applicationId, cancellationToken);

        if (await recruitment.LiveOfferForAsync(applicationId, cancellationToken) is { } already)
        {
            throw new InvalidOperationException(
                already.Status == OfferStatus.Drafted
                    ? "There is already a draft offer for this application. Change that one, or "
                      + "withdraw it first."
                    : "There is already an offer out with this candidate. Withdraw it before "
                      + "writing another, so there is only ever one set of terms they could "
                      + "have accepted.");
        }

        var secret = NewSecret();

        var offer = Offer.Write(
            applicationId,
            jobTitle,
            salary,
            frequency,
            startsOn,
            closesOn,
            terms,
            Fingerprint(secret),
            madeById,
            clock.Now);

        recruitment.Add(offer);
        await recruitment.SaveAsync(cancellationToken);

        return new WrittenOffer(offer, secret);
    }

    public async Task ReviseAsync(
        Guid offerId,
        string jobTitle,
        Money salary,
        PayFrequency frequency,
        DateOnly startsOn,
        DateOnly closesOn,
        string terms,
        CancellationToken cancellationToken = default)
    {
        var offer = await Required(offerId, cancellationToken);

        offer.Revise(jobTitle, salary, frequency, startsOn, closesOn, terms);

        await recruitment.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Record that it has gone out, which freezes the terms.
    /// </summary>
    /// <remarks>
    /// Separate from actually sending the email, because the two fail differently: an email that
    /// bounces is somebody typing the address again, and an offer marked sent that was not is a
    /// candidate waiting for something nobody posted. Whoever calls this has the link.
    /// </remarks>
    public async Task SendAsync(Guid offerId, CancellationToken cancellationToken = default)
    {
        var offer = await Required(offerId, cancellationToken);

        offer.Sent(clock.Now);

        await recruitment.SaveAsync(cancellationToken);
    }

    /// <summary>Give somebody longer to answer.</summary>
    public async Task CloseOnAsync(
        Guid offerId, DateOnly closesOn, CancellationToken cancellationToken = default)
    {
        var offer = await Required(offerId, cancellationToken);

        offer.CloseOn(closesOn);

        await recruitment.SaveAsync(cancellationToken);
    }

    public async Task WithdrawAsync(
        Guid offerId, string why, CancellationToken cancellationToken = default)
    {
        var offer = await Required(offerId, cancellationToken);

        offer.Withdraw(why, clock.Now);

        await recruitment.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// The offer behind a link, for somebody with no account.
    /// </summary>
    /// <remarks>
    /// Looked up by the hash of what was presented, so the secret is never compared against
    /// anything stored — there is nothing stored to compare it against. A wrong or made-up link
    /// finds nothing, which is the same answer as a link to an offer that no longer exists, and
    /// that is deliberate: a page that distinguished the two would tell whoever is guessing that
    /// they were close.
    /// </remarks>
    public Task<Offer?> BehindAsync(string secret, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(secret)
            ? Task.FromResult<Offer?>(null)
            : recruitment.OfferByHashAsync(Fingerprint(secret.Trim()), cancellationToken);

    /// <summary>
    /// The candidate accepts.
    /// </summary>
    /// <remarks>
    /// Takes the secret rather than the offer's identifier, because the caller is a public page
    /// and the secret is the only thing that authorises this. An identifier would mean the page
    /// had to trust something it was given a moment earlier.
    /// </remarks>
    public async Task<Offer> AcceptAsync(
        string secret, string signedName, CancellationToken cancellationToken = default)
    {
        var offer = await BehindAsync(secret, cancellationToken)
            ?? throw new InvalidOperationException(NoSuchLink);

        offer.Accepted(signedName, clock.Today, clock.Now);

        await recruitment.SaveAsync(cancellationToken);

        return offer;
    }

    public async Task<Offer> DeclineAsync(
        string secret, string? why, CancellationToken cancellationToken = default)
    {
        var offer = await BehindAsync(secret, cancellationToken)
            ?? throw new InvalidOperationException(NoSuchLink);

        offer.Declined(why, clock.Today, clock.Now);

        await recruitment.SaveAsync(cancellationToken);

        return offer;
    }

    /// <summary>
    /// Turn an accepted offer into somebody who works here.
    /// </summary>
    /// <remarks>
    /// The one place in this system where a candidate becomes an employee, and it is a button
    /// somebody presses rather than something acceptance does on its own — see the remarks on
    /// <see cref="Offer"/>.
    ///
    /// Three things happen together, and they have to: the staff record is created from the
    /// candidate and the offer, the onboarding checklist is started against it, and the
    /// application is marked hired so the requisition's headcount comes down. Any two of those
    /// without the third is a state somebody has to notice and fix — a person on the staff list
    /// with no checklist, or a requisition still advertising a post that is filled.
    ///
    /// The terms are written onto the staff record from the offer, which is the point of having
    /// recorded them. Retyping a salary that is already on the screen in front of somebody is
    /// how it gets retyped wrong.
    /// </remarks>
    public async Task<Employee> TakeOnAsync(
        Guid offerId,
        Guid? departmentId = null,
        Guid? reportsToId = null,
        CancellationToken cancellationToken = default)
    {
        var offer = await Required(offerId, cancellationToken);

        if (offer.Status != OfferStatus.Accepted)
        {
            throw new InvalidOperationException(
                "Only an accepted offer becomes a member of staff.");
        }

        if (offer.EmployeeId is not null)
        {
            throw new InvalidOperationException(
                "A staff record has already been made from this offer. Two would be two people "
                + "with one name, one of whom would be paid.");
        }

        var application = await RequiredApplication(offer.ApplicationId, cancellationToken);

        var candidate = await recruitment.FindCandidateAsync(
                application.CandidateId, cancellationToken)
            ?? throw new InvalidOperationException("That candidate is no longer on record.");

        var employee = Employee.Hire(
            candidate.FullName, offer.StartsOn, departmentId, offer.JobTitle);

        /*
         * The terms come off the offer rather than being typed again. They are already on the
         * screen in front of whoever is pressing this, and retyping a salary that is already
         * recorded is how it gets retyped wrong — three money boxes in this application once
         * stored a hundredth of what somebody meant.
         *
         * Permanent rather than asked for, because the offer does not carry a contract type and
         * inventing a dropdown here would put a decision in the wrong place. It is one field on
         * the staff record and the person who just created that record is looking at it.
         */
        employee.Agree(new EmploymentTerms
        {
            Contract = ContractType.Permanent,
            Frequency = offer.Frequency,
            SalaryMinorUnits = offer.SalaryMinorUnits,
            SalaryCurrency = offer.SalaryCurrency,
        });

        if (reportsToId is { } manager)
        {
            employee.ReportsTo(manager);
        }

        people.Add(employee);
        people.Add(Onboarding.Begin(employee.Id, offer.StartsOn, clock.Now));

        offer.Became(employee.Id);

        application.MoveTo(ApplicationStatus.Hired, clock.Now);

        var posting = await recruitment.FindPostingAsync(
                application.PostingId, cancellationToken)
            ?? throw new InvalidOperationException("That advert is no longer on record.");

        var requisition = await recruitment.FindRequisitionAsync(
                posting.RequisitionId, cancellationToken)
            ?? throw new InvalidOperationException("That requisition is no longer on record.");

        if (requisition.CanHire)
        {
            requisition.RecordHire(clock.Now);
        }

        /*
         * One save covers all of it, because both repositories are the same DbContext. Saving
         * the staff record and then the application separately would leave, on a failure, a
         * person on the staff list who does not appear to have been hired against anything.
         */
        await recruitment.SaveAsync(cancellationToken);

        return employee;
    }

    private const string NoSuchLink =
        "That link is not one we recognise. It may have been withdrawn, or it may have been "
        + "mistyped — check the address against the one in the email.";

    /// <summary>
    /// Thirty-two random bytes, base64url.
    /// </summary>
    /// <remarks>
    /// The same 256 bits an API key gets, and for the same reason: this secret is the only thing
    /// standing between a stranger and accepting a job in somebody else's name. No prefix,
    /// unlike a key — there is nothing to recognise it in, because it only ever appears in one
    /// URL.
    /// </remarks>
    private static string NewSecret() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// SHA-256 of the secret, hex.
    /// </summary>
    /// <remarks>
    /// A fast hash, as an API key's is, and for the argument written out there: this is 256 bits
    /// of randomness rather than a password somebody chose, so there is no dictionary to slow
    /// anybody down against.
    /// </remarks>
    private static string Fingerprint(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private async Task<Offer> Required(Guid id, CancellationToken cancellationToken) =>
        await recruitment.FindOfferAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That offer no longer exists.");

    private async Task<JobApplication> RequiredApplication(
        Guid id, CancellationToken cancellationToken) =>
        await recruitment.FindApplicationAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no application with that identifier.");
}

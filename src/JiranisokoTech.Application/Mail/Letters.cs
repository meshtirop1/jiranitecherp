using System.Net;
using System.Text;

namespace JiranisokoTech.Application.Mail;

/// <summary>
/// The messages this system sends, written once.
/// </summary>
/// <remarks>
/// Plain string building rather than a templating engine. There are a handful
/// of messages, they are short, and a template engine would add a second place
/// to look for the words along with a class of runtime failure — a template
/// that will not parse — that this cannot have.
///
/// Everything interpolated into HTML goes through <see cref="Escape"/>. A
/// person's name is data somebody typed, and a display name of
/// &lt;script&gt; in an email that a colleague opens is the same injection as
/// on a page, arriving somewhere nobody is looking for it.
/// </remarks>
public static class Letters
{
    private const string Signature = "Jiranisoko Tech Solutions";

    /// <summary>An account has been opened and needs a password.</summary>
    public static EmailMessage Invitation(
        string toAddress, string toName, string link, string openedBy)
    {
        var text = $"""
            Hello {toName},

            {openedBy} has opened an account for you on the Jiranisoko Tech
            delivery system.

            Choose a password here:

            {link}

            Nobody else knows your password and nobody else can set it. If you
            did not expect this, tell {openedBy} — do not follow the link.

            {Signature}
            """;

        return Compose(toAddress, toName, "Your Jiranisoko Tech account", text, link);
    }

    /// <summary>
    /// Somebody asked for a way back into their own account.
    /// </summary>
    /// <remarks>
    /// Says nothing about who asked or from where. The request arrives from an anonymous
    /// form, so the only thing this system knows about the asker is an address they typed —
    /// and printing "requested from 41.90.x.x" in a letter to somebody who did not ask reads
    /// as an accusation built on a guess.
    ///
    /// It does tell them what to do if it was not them, and the advice is to ignore it rather
    /// than to press anything. A link in a letter to somebody who did not ask for one is a
    /// link that should stay unpressed, and "tell us" gives them a job they cannot do at
    /// seven on a Sunday.
    /// </remarks>
    public static EmailMessage PasswordReset(string toAddress, string toName, string link)
    {
        var text = $"""
            Hello {toName},

            Somebody asked for a way back into your Jiranisoko Tech account.
            Choose a new password here:

            {link}

            If that was not you, nothing has happened yet and you do not need to
            do anything — the link only works once somebody follows it, and your
            current password still works until then.

            {Signature}
            """;

        return Compose(toAddress, toName, "A way back into your account", text, link);
    }

    /// <summary>
    /// The account was used somewhere it has not been used before.
    /// </summary>
    /// <remarks>
    /// A notice and not an alarm, and the wording is the whole of the design. This fires
    /// for a new laptop as readily as for a stolen password, so a message that shouted
    /// would train somebody to ignore the one that mattered. It says what happened, says
    /// what to do if it was not them, and does not ask them to confirm anything — there
    /// is no link to click, because teaching people to click links in security emails is
    /// how the next attack succeeds.
    /// </remarks>
    public static EmailMessage SignedInSomewhereNew(
        string toAddress, string toName, string browser, string address, DateTimeOffset at)
    {
        var text = $"""
            Hello {toName},

            Your Jiranisoko Tech account was just used to sign in from somewhere it
            has not been used before.

              When:    {at:dddd d MMMM yyyy 'at' HH:mm} UTC
              Browser: {browser}
              Address: {address}

            If that was you — a new laptop, a different office, a phone — there is
            nothing to do.

            If it was not you, sign in, change your password, and use "sign out
            everywhere" on your account page. Then tell whoever administers this
            system.

            There is no link in this message on purpose. Go to the system the way
            you normally do.

            {Signature}
            """;

        return Compose(
            toAddress, toName, "Your account was used somewhere new", text, link: null);
    }

    /// <summary>
    /// Qualifications that lapse in the next two months.
    /// </summary>
    /// <remarks>
    /// A list in one message rather than one message per qualification. Three separate
    /// emails about three certificates is three things to dismiss; one list is a thing to
    /// act on.
    /// </remarks>
    public static EmailMessage QualificationsLapsing(
        string toAddress, string toName, IReadOnlyList<string> lines)
    {
        var listed = string.Join(Environment.NewLine, lines);

        var text = $"""
            Hello {toName},

            These qualifications lapse within the next two months:

            {listed}

            Two months is about what it takes to book and sit most examinations.
            A qualification nobody renews is discovered at the moment a client asks
            for evidence of it.

            {Signature}
            """;

        return Compose(
            toAddress, toName, "Qualifications lapsing soon", text, link: null);
    }

    /// <summary>Client contracts that run out in the next six weeks.</summary>
    /// <remarks>
    /// Work continuing past the end of the contract that authorises it is work a client
    /// can decline to pay for, and it is otherwise discovered at the invoice rather than
    /// at the date.
    /// </remarks>
    public static EmailMessage ContractsExpiring(
        string toAddress, string toName, IReadOnlyList<string> lines)
    {
        var listed = string.Join(Environment.NewLine, lines);

        var text = $"""
            Hello {toName},

            These client contracts run out within the next six weeks:

            {listed}

            Work done after a contract ends is work the client can decline to pay
            for, and that is usually noticed at the invoice rather than at the date.

            {Signature}
            """;

        return Compose(toAddress, toName, "Contracts running out soon", text, link: null);
    }

    /// <summary>
    /// Domains, certificates and subscriptions running out.
    /// </summary>
    /// <remarks>
    /// Blunter than the contracts letter, because the consequences are blunter: a contract that
    /// lapses is a conversation the following week, and a certificate that expires is a site
    /// nobody can reach at a moment nobody chose.
    /// </remarks>
    public static EmailMessage ResourcesExpiring(
        string toAddress, string toName, IReadOnlyList<string> lines)
    {
        var listed = string.Join(Environment.NewLine, lines);

        var text = $"""
            Hello {toName},

            These run out soon:

            {listed}

            A certificate that expires takes the site down at a moment nobody chose,
            and a domain that lapses takes the firm's email with it. Both are cheap
            to renew and expensive to notice late.

            {Signature}
            """;

        return Compose(toAddress, toName, "Things running out soon", text, link: null);
    }

    /// <summary>
    /// One thing happened, and somebody thought this person would want to know.
    /// </summary>
    /// <remarks>
    /// Deliberately the plainest letter in this file: one line and a link. It carries the same
    /// sentence the notice centre shows, because two wordings of one fact is two things to keep
    /// in step and one of them will drift.
    /// </remarks>
    public static EmailMessage SomethingHappened(
        string toAddress, string toName, string subject, string? link)
    {
        var text = link is { Length: > 0 }
            ? $"""
                Hello {toName},

                {subject}

                {link}

                {Signature}
                """
            : $"""
                Hello {toName},

                {subject}

                {Signature}
                """;

        return Compose(toAddress, toName, subject, text, link);
    }

    /// <summary>Somebody is waiting on this person to decide something.</summary>
    public static EmailMessage AwaitingDecision(
        string toAddress,
        string toName,
        string what,
        string askedBy,
        string link)
    {
        var text = $"""
            Hello {toName},

            {askedBy} has asked for {what}, and it is waiting on you.

            {link}

            {Signature}
            """;

        return Compose(toAddress, toName, $"Waiting on you: {what}", text, link);
    }

    /// <summary>A chain has finished, and whoever asked needs telling.</summary>
    public static EmailMessage Decided(
        string toAddress,
        string toName,
        string what,
        bool approved,
        string? because,
        string link)
    {
        var outcome = approved ? "approved" : "not approved";

        var reason = string.IsNullOrWhiteSpace(because)
            ? string.Empty
            : $"\n\nReason given: {because}";

        var text = $"""
            Hello {toName},

            Your request for {what} has been {outcome}.{reason}

            {link}

            {Signature}
            """;

        return Compose(
            toAddress, toName, $"{what} — {outcome}", text, link);
    }

    /// <summary>
    /// The HTML body, made from the text body.
    /// </summary>
    /// <remarks>
    /// Written this way round on purpose: a text part stripped out of HTML
    /// reads like a stripped-out HTML body, while HTML built from a letter
    /// somebody wrote reads like a letter. Blank lines become paragraphs and
    /// the link becomes a link; nothing else is added.
    ///
    /// No images, no tracking pixel, no stylesheet. A message with a remote
    /// image in it reports back when it was opened and from where, which is not
    /// something this firm needs to know about its own staff.
    /// </remarks>
    /*
     * The four letters a candidate receives.
     *
     * A candidate is a member of the public, not a user of this system, and
     * that governs every word below. None of them carries a scorecard, a
     * panel's reasoning, an internal note, another candidate, or anything about
     * how the decision was reached. A rejection in particular says the firm is
     * not taking it further and thanks them for the time: the reasons are an
     * internal record about a person, and an email is the one artefact certain
     * to be forwarded.
     *
     * They are also the only letters this system sends to somebody who did not
     * ask for an account, so each one says which advert it is about. An
     * unattributed "we are not taking this further" to somebody who applied to
     * four firms is worse than silence.
     */

    /// <summary>We have it. Sent the moment an application arrives.</summary>
    /// <remarks>
    /// The cheapest letter here and the one that matters most. Somebody who
    /// applies and hears nothing concludes within a week that the advert was
    /// stale or the form was broken, and tells other people so.
    /// </remarks>
    public static EmailMessage ApplicationReceived(
        string toAddress, string toName, string jobTitle)
    {
        var text = $"""
            Hello {toName},

            Thank you for applying for {jobTitle}. Your application has reached
            us and somebody will read it.

            We will write again either way, whether or not we take it further.
            You do not need to do anything in the meantime.

            {Signature}
            """;

        return Compose(toAddress, toName, $"Your application for {jobTitle}", text, null);
    }

    /// <summary>Not taking it further.</summary>
    /// <remarks>
    /// Short, and says nothing about why. The reasons live in scorecards that
    /// are about a person and were written for colleagues; putting any of them
    /// in a letter turns a private assessment into a document the subject
    /// holds. It also invites an argument the firm cannot win and is not
    /// obliged to have.
    /// </remarks>
    public static EmailMessage ApplicationRefused(
        string toAddress, string toName, string jobTitle)
    {
        var text = $"""
            Hello {toName},

            Thank you for applying for {jobTitle}, and for the time you put into
            it. We are not taking your application further on this occasion.

            We do keep applications on file, and you are welcome to apply again
            for anything else we advertise.

            {Signature}
            """;

        /*
         * A different subject from the acknowledgement, which says "Your
         * application for X". Sharing one would thread the two together in a
         * mail client, which sounds tidy and is not: the second message then
         * arrives looking like a duplicate of the first, and the one that
         * actually carries the answer is the one collapsed out of sight.
         */
        return Compose(toAddress, toName, $"About your application for {jobTitle}", text, null);
    }

    /// <summary>Come and talk to us.</summary>
    /// <remarks>
    /// Carries the date, the kind of conversation and where — and not who is on
    /// the panel. Naming the panel invites somebody to look them up and arrive
    /// having prepared for individuals rather than for the job, and it exposes
    /// staff to a stranger before anybody has met.
    /// </remarks>
    public static EmailMessage InterviewInvitation(
        string toAddress,
        string toName,
        string jobTitle,
        DateTimeOffset when,
        string? where)
    {
        var place = string.IsNullOrWhiteSpace(where)
            ? "We will confirm where nearer the time."
            : $"Where: {where}";

        var text = $"""
            Hello {toName},

            We would like to talk to you about {jobTitle}.

            When: {when:dddd d MMMM yyyy} at {when:HH:mm}

            {place}

            If that time does not work, reply to this message and we will find
            another. Nothing is expected of you beforehand.

            {Signature}
            """;

        return Compose(toAddress, toName, $"Interview for {jobTitle}", text, null);
    }

    /// <summary>We would like you to join.</summary>
    /// <remarks>
    /// Deliberately not a contract and does not pretend to be one. It says an
    /// offer is coming and that the terms follow separately, because the terms
    /// are a document somebody signs and an email that reads like one is an
    /// email somebody will later say they accepted.
    /// </remarks>
    public static EmailMessage Offer(string toAddress, string toName, string jobTitle)
    {
        var text = $"""
            Hello {toName},

            We would like to offer you the {jobTitle} position.

            Somebody will be in touch shortly with the terms in writing — the
            salary, the start date and the rest — and nothing is settled until
            you have those and have agreed to them.

            We are glad you applied.

            {Signature}
            """;

        return Compose(toAddress, toName, $"An offer for {jobTitle}", text, null);
    }

    /// <summary>
    /// The offer itself, with the link to read and answer it.
    /// </summary>
    /// <remarks>
    /// The other half of the promise <see cref="Offer"/> makes. That letter says an offer is
    /// coming and that the terms follow separately, on the grounds that an email reading like a
    /// contract is an email somebody will later say they accepted — and it left the firm owing
    /// somebody a letter nobody wrote. This is that letter, and it still carries no terms: the
    /// terms are on a page, where they are recorded as having been shown.
    ///
    /// The salary is deliberately not in the body. An email is forwarded, quoted and left open
    /// on screens; the page behind the link is reached once, by whoever holds the secret.
    /// </remarks>
    public static EmailMessage OfferTerms(
        string toAddress, string toName, string jobTitle, string link)
    {
        var text = $"""
            Hello {toName},

            Here is our offer for the {jobTitle} position, in full — the salary, the
            start date and the terms.

            {link}

            You can accept or turn it down on that page. The link is yours alone,
            so please do not forward it.

            {Signature}
            """;

        return Compose(toAddress, toName, $"Your offer for {jobTitle}", text, link);
    }

    private static EmailMessage Compose(
        string toAddress, string toName, string subject, string text, string? link)
    {
        var body = new StringBuilder("<div style=\"font-family:system-ui,sans-serif;"
            + "font-size:15px;line-height:1.5;color:#1f2937\">");

        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var tidied = paragraph.Replace("\r\n", " ").Replace('\n', ' ').Trim();

            if (link is not null && tidied == link)
            {
                var safe = Escape(link);

                body.Append($"<p><a href=\"{safe}\">{safe}</a></p>");

                continue;
            }

            body.Append($"<p>{Escape(tidied)}</p>");
        }

        body.Append("</div>");

        return new EmailMessage(toAddress, toName, subject, Tidy(text), body.ToString());
    }

    /// <summary>
    /// The text body with its source indentation removed.
    /// </summary>
    /// <remarks>
    /// Raw string literals keep the indentation of the code they sit in, and a
    /// message that arrives indented four spaces looks like a mistake.
    /// </remarks>
    private static string Tidy(string text) =>
        string.Join(
            Environment.NewLine,
            text.Split('\n').Select(line => line.TrimEnd()));

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}

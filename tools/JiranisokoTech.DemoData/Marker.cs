namespace JiranisokoTech.DemoData;

/// <summary>
/// How every row this tool writes says out loud that it is not real.
/// </summary>
/// <remarks>
/// Section 83's last sentence is the whole of this file: "Seed data must be clearly identifiable
/// as development/demo data."
///
/// <b>The hard part is that the same binary runs in production.</b> This is one firm's real ERP,
/// not a product with a demo tier — <c>EnvironmentBanner</c> marks the copy of the application and
/// nothing marks the data, so a demonstration row restored or copied into the live database would
/// be indistinguishable from a real one afterwards. An environment variable would not help: it is
/// a fact about where the tool ran, and the row outlives the run.
///
/// So the marking is in the values themselves, in five places, and every one of them is somewhere
/// a person looks:
///
/// the firm's own trading name, which is on every invoice and every offer letter; the invoice
/// prefix, so every document number reads DEMO-0001; the code or slug on every client, project,
/// department, advert and asset; the e-mail domain, which is a reserved one that cannot receive
/// anything; and the actor on every line of the audit trail.
///
/// Five rather than one because each covers a different way somebody meets the data. Reading a
/// page shows the trading name. Reading a PDF shows the invoice number. Querying the database
/// shows the codes. Watching the mail queue shows the domain. Reading the trail shows who did it.
/// Any one of them alone leaves a route by which a demonstration row looks real.
/// </remarks>
public static class Marker
{
    /// <summary>
    /// The firm's trading name, which appears on every invoice and every offer letter.
    /// </summary>
    /// <remarks>
    /// In brackets and in capitals, at the end, so it survives being truncated in a narrow column
    /// less often than a prefix would — and so somebody skimming a page sees the real name first
    /// and the warning immediately after, rather than having to read past the warning to find out
    /// what they are looking at.
    /// </remarks>
    public const string TradingName = "Jiranisoko Tech Solutions (DEMONSTRATION DATA)";

    public const string LegalName = "Jiranisoko Tech Solutions Ltd (DEMONSTRATION DATA)";

    /// <summary>
    /// What every invoice number begins with.
    /// </summary>
    /// <remarks>
    /// The firm's own prefix is a settings value with eight characters to play with, and every
    /// invoice number in the database is built from it. So DEMO-0001 is on the document, in the
    /// ledger, in the reminder letter and in anything anybody exports — which is the one marker
    /// that travels outside this application.
    /// </remarks>
    public const string InvoicePrefix = "DEMO";

    /// <summary>
    /// What every code, slug and tag this tool writes begins with.
    /// </summary>
    /// <remarks>
    /// Lower case for slugs and codes, upper for asset tags, because the domain upper-cases a tag
    /// and slugifies a code — following each rather than fighting it means the marker survives.
    /// </remarks>
    public const string Prefix = "demo-";

    /// <summary>
    /// The domain every e-mail address this tool writes ends at.
    /// </summary>
    /// <remarks>
    /// <c>.invalid</c> is reserved by RFC 2606 precisely for this: it is guaranteed never to
    /// resolve, so a letter addressed to one of these cannot reach a person even if the mail
    /// transport is switched on by accident. A real-looking domain would eventually send a
    /// demonstration invoice reminder to somebody who owns it.
    /// </remarks>
    public const string EmailDomain = "demo.invalid";

    /// <summary>
    /// Who the audit trail says did all of this.
    /// </summary>
    /// <remarks>
    /// <c>ICurrentUser.Name</c> is copied into every audit entry and has no foreign key behind it,
    /// so a named actor costs nothing and answers the question somebody reading the trail in a year
    /// will actually ask — not "who was this" but "why is there a year of history nobody remembers
    /// making".
    /// </remarks>
    public const string Actor = "the demonstration seed";

    /// <summary>The currency, and there is only one on purpose.</summary>
    /// <remarks>
    /// <c>Money</c> refuses to add two amounts in different currencies, and <c>Tally</c> answers
    /// AcrossCurrencies rather than a figure when a total spans them — which is correct and is
    /// exactly what would make every headline on the dashboard read "in more than one currency"
    /// if this tool seeded two. A demonstration database whose every total refuses to be a number
    /// demonstrates nothing, so this stays one currency and the multi-currency behaviour is left
    /// to the tests that exist for it.
    /// </remarks>
    public const string Currency = "KES";

    /// <summary>
    /// The webhook secret the seed configures and then types into the connect form.
    /// </summary>
    /// <remarks>
    /// Connecting a repository compares the supplied secret in fixed time against the one the
    /// application is configured with, and refuses a mismatch — which is the right rule, because a
    /// repository connected with the wrong secret is one whose every delivery is refused and the
    /// connect form is the last moment anybody would notice. So the seed configures a value for
    /// every host and hands the same one back.
    ///
    /// It is not a secret. Only its hash reaches the database, and the demonstration repositories
    /// it connects will never receive a delivery because nothing outside this machine knows they
    /// exist.
    /// </remarks>
    public const string GitSecret = "the-demonstration-seed-webhook-secret";

    /// <summary>A code, slug or tag, marked.</summary>
    public static string Code(string name) => Prefix + name;

    /// <summary>An address at the reserved domain, marked by construction.</summary>
    public static string Email(string local) => $"{local}@{EmailDomain}";
}

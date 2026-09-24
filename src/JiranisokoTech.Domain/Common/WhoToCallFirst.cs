namespace JiranisokoTech.Domain.Common;

/// <summary>
/// A person in a contact book, whichever book it is.
/// </summary>
/// <remarks>
/// Implemented by <see cref="Clients.Contact"/> and <see cref="Vendors.VendorContact"/>. It
/// exists so that the one rule both books share can be written once — see
/// <see cref="WhoToCallFirst"/> — rather than copied into a second service where it would then
/// be changed in one place and not the other.
///
/// Deliberately narrow: it names only what the rule touches. A wider interface would invite the
/// two contact books to converge on one type, and they are separate for reasons the vendor
/// contact's own remarks set out.
/// </remarks>
public interface IContactInABook
{
    Guid Id { get; }

    /// <summary>
    /// Their name.
    /// </summary>
    /// <remarks>
    /// On the interface so the refusal can say "Wanjiku Mwangi has left" rather than "they have
    /// left". When this rule was first extracted it could not, and a test written long before it
    /// caught the regression — a message that names the person is the difference between a
    /// refusal somebody acts on and one they read twice.
    /// </remarks>
    string Name { get; }

    /// <summary>The one to ring first.</summary>
    bool IsMain { get; }

    /// <summary>Still at that company.</summary>
    bool IsHere { get; }

    /// <summary>When they were added, which is what decides who inherits the post.</summary>
    DateTimeOffset AddedAt { get; }

    void Main(bool isMain);
}

/// <summary>
/// Exactly one person in a contact book is the one to call first.
/// </summary>
/// <remarks>
/// <b>A policy, written once.</b> Client contacts and vendor contacts are separate entities in
/// separate tables — see <see cref="Vendors.VendorContact"/> for why — but they answer to the
/// same rule, and a rule that exists twice is one that gets changed once. The shape is repeated
/// because shape is cheap; this is not shape.
///
/// It lives in the domain rather than in a service because it needs no database: given the
/// contacts, it decides. What a service still has to do is fetch them and save afterwards.
/// </remarks>
public static class WhoToCallFirst
{
    /// <summary>
    /// Make one person the one to call, and clear whoever held it.
    /// </summary>
    /// <remarks>
    /// Iterated over a copy, because clearing the flag is a write to a tracked entity and the
    /// sequence it came from is usually a repository's own query result — mutating entities while
    /// enumerating the list EF handed back is how a perfectly correct-looking loop throws.
    ///
    /// Somebody who has left cannot take the post. The caller is usually promoting a person they
    /// just added, so this rarely fires; it matters on the screen where a row for somebody who
    /// left last year still has a promote button on it.
    /// </remarks>
    public static void Promote<TContact>(IEnumerable<TContact> book, Guid contactId)
        where TContact : IContactInABook
    {
        var everybody = book.ToList();

        var chosen = everybody.FirstOrDefault(one => one.Id == contactId)
            ?? throw new InvalidOperationException("That person is not in this contact book.");

        if (!chosen.IsHere)
        {
            throw new InvalidOperationException(
                $"{chosen.Name} has left. Somebody who is not there cannot be the first person "
                + "to call, and an address that bounces is worse than no address at all.");
        }

        foreach (var other in everybody.Where(one => one.IsMain && one.Id != contactId))
        {
            other.Main(false);
        }

        chosen.Main(true);
    }

    /// <summary>
    /// Hand the post on when whoever held it leaves.
    /// </summary>
    /// <remarks>
    /// To the longest-serving of whoever is still there. Any rule would do; having no rule would
    /// not, because a contact book with nobody marked is one where the next person to write to
    /// that company has to guess — and the post quietly staying with somebody who has left is how
    /// an order goes to a dead address for a year.
    ///
    /// Does nothing when the person leaving did not hold the post, and does nothing when nobody
    /// is left. The second is a real state: the last contact at a supplier leaves, and the honest
    /// answer is that there is nobody to call.
    /// </remarks>
    public static void Inherit<TContact>(IEnumerable<TContact> book, Guid whoLeft)
        where TContact : IContactInABook
    {
        var next = book
            .Where(one => one.IsHere && one.Id != whoLeft)
            .OrderBy(one => one.AddedAt)
            .ThenBy(one => one.Id)
            .FirstOrDefault();

        next?.Main(true);
    }
}

using JiranisokoTech.Domain.Documents;

namespace JiranisokoTech.Tests.Documents;

/// <summary>
/// Versions and tags on an attached document.
/// </summary>
/// <remarks>
/// Section 24 had attachments with no versions, so replacing a signed contract meant either
/// deleting the old one — destroying the evidence of what was agreed before — or attaching a
/// second file with no way to say which was current. Both happen; the second is worse,
/// because the list then shows two contracts and nobody can tell which the client signed.
/// </remarks>
public class DocumentVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    private static Attachment Document() => Attachment.Of(
        AttachedTo.Client,
        Guid.CreateVersion7(),
        "terms-2026.pdf",
        "stored-name",
        4096,
        Guid.CreateVersion7(),
        Now,
        note: null);

    [Fact]
    public void A_document_is_current_until_something_replaces_it()
    {
        var document = Document();

        Assert.True(document.IsCurrent);
        Assert.Null(document.SupersededAt);

        document.SupersededBy(Guid.CreateVersion7(), Now);

        Assert.False(document.IsCurrent);
        Assert.Equal(Now, document.SupersededAt);
    }

    /// <summary>
    /// A document already replaced is not replaced again.
    /// </summary>
    /// <remarks>
    /// Otherwise the chain forks: two documents both claiming to supersede one, and walking
    /// it forwards finds whichever was written last. The service refuses this at the door and
    /// the entity refuses it again, because a fork is silent and permanent.
    /// </remarks>
    [Fact]
    public void A_document_already_replaced_is_not_replaced_again()
    {
        var document = Document();
        var first = Guid.CreateVersion7();

        document.SupersededBy(first, Now);
        document.SupersededBy(Guid.CreateVersion7(), Now.AddDays(1));

        Assert.Equal(first, document.SupersededById);
        Assert.Equal(Now, document.SupersededAt);
    }

    /// <summary>
    /// Being replaced is announced.
    /// </summary>
    /// <remarks>
    /// A superseded contract is a fact the rest of the firm may care about: the terms
    /// somebody is billing against have moved, and an invoice quoting the old reference is
    /// quoting a document nobody should act on.
    /// </remarks>
    [Fact]
    public void Being_replaced_is_announced()
    {
        var document = Document();
        document.ClearEvents();

        document.SupersededBy(Guid.CreateVersion7(), Now);

        Assert.IsType<DocumentSuperseded>(Assert.Single(document.Events));
    }

    /// <summary>
    /// Tags are lower-cased and de-duplicated.
    /// </summary>
    /// <remarks>
    /// "Contract" and "contract" are one tag to everybody except a string comparison, and a
    /// document carrying both would appear twice in any list built from the words.
    /// </remarks>
    [Fact]
    public void Tags_are_reduced_to_distinct_lower_case_words()
    {
        var document = Document();

        document.Tagged("Contract signed CONTRACT  2026 ");

        Assert.Equal("contract signed 2026", document.Tags);
    }

    /// <summary>Commas and semicolons separate tags as readily as spaces.</summary>
    /// <remarks>
    /// Because somebody typing a list of things types commas, and an importer that only
    /// understood spaces would silently produce one tag called "contract,signed".
    /// </remarks>
    [Theory]
    [InlineData("contract, signed")]
    [InlineData("contract;signed")]
    [InlineData("contract  signed")]
    public void Tags_are_separated_by_whatever_somebody_typed(string typed)
    {
        var document = Document();

        document.Tagged(typed);

        Assert.Equal("contract signed", document.Tags);
    }

    [Fact]
    public void Clearing_the_tags_leaves_nothing_rather_than_an_empty_string()
    {
        var document = Document();

        document.Tagged("contract");
        document.Tagged("   ");

        Assert.Null(document.Tags);
    }

    /// <summary>
    /// A great many tags are capped rather than stored.
    /// </summary>
    /// <remarks>
    /// The column has a length, and a document pasted full of words would fail to save — so
    /// the cap is here, where it can drop the excess quietly, rather than at the database
    /// where it would lose the whole document.
    /// </remarks>
    [Fact]
    public void Too_many_tags_are_capped()
    {
        var document = Document();

        document.Tagged(string.Join(' ', Enumerable.Range(1, 60).Select(one => $"tag{one}")));

        Assert.Equal(20, document.Tags!.Split(' ').Length);
    }
}

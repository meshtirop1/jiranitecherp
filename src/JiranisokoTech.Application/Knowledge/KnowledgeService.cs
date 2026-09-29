using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Knowledge;

namespace JiranisokoTech.Application.Knowledge;

/// <summary>What the knowledge base needs read and written.</summary>
public interface IKnowledgeRepository
{
    Task<Article?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Article?> ByKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Addresses already taken that begin with this one.</summary>
    /// <remarks>
    /// A prefix read rather than an existence check, because settling an address means knowing
    /// which suffixes have gone — and asking one at a time would be a query per attempt.
    /// </remarks>
    Task<List<string>> KeysLikeAsync(string key, CancellationToken cancellationToken = default);

    void Add(Article article);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Writing down what the firm knows.
/// </summary>
/// <remarks>
/// Section 25. Thin, because nearly everything worth refusing is refused on
/// <see cref="Article"/> itself, where it needs no other row to decide. What lives here is the
/// one thing that does: settling the address, which is a question about every other article.
/// </remarks>
public sealed class KnowledgeService(IKnowledgeRepository articles, IClock clock)
{
    /// <summary>How many articles may share a title before it is somebody's problem.</summary>
    private const int Most = 100;

    public async Task<Article> StartAsync(
        string title,
        string summary,
        string body,
        Guid ownerId,
        string? labels = null,
        CancellationToken cancellationToken = default)
    {
        var article = Article.Start(
            await AnAddressNothingElseHas(title, cancellationToken),
            title,
            summary,
            body,
            ownerId,
            clock.Now,
            labels);

        articles.Add(article);
        await articles.SaveAsync(cancellationToken);

        return article;
    }

    public async Task RewriteAsync(
        Guid id,
        string title,
        string summary,
        string body,
        string? labels,
        CancellationToken cancellationToken = default)
    {
        var article = await Required(id, cancellationToken);

        article.Rewrite(title, summary, body, labels);

        await articles.SaveAsync(cancellationToken);
    }

    public async Task PublishAsync(
        Guid id,
        Guid byEmployeeId,
        DateOnly reviewBy,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var article = await Required(id, cancellationToken);

        article.Publish(byEmployeeId, reviewBy, clock.Now, note);

        await articles.SaveAsync(cancellationToken);
    }

    public async Task StillTrueAsync(
        Guid id,
        Guid byEmployeeId,
        DateOnly reviewBy,
        CancellationToken cancellationToken = default)
    {
        var article = await Required(id, cancellationToken);

        article.StillTrue(byEmployeeId, reviewBy, clock.Now);

        await articles.SaveAsync(cancellationToken);
    }

    public async Task RetireAsync(
        Guid id, string because, CancellationToken cancellationToken = default)
    {
        var article = await Required(id, cancellationToken);

        article.Retire(because, clock.Now);

        await articles.SaveAsync(cancellationToken);
    }

    public async Task HandOverAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken = default)
    {
        var article = await Required(id, cancellationToken);

        article.Owner(ownerId);

        await articles.SaveAsync(cancellationToken);
    }

    public Task<Article?> OneAsync(Guid id, CancellationToken cancellationToken = default) =>
        articles.FindAsync(id, cancellationToken);

    public Task<Article?> ByKeyAsync(
        string key, CancellationToken cancellationToken = default) =>
        articles.ByKeyAsync(key, cancellationToken);

    /// <summary>
    /// An address nothing else has.
    /// </summary>
    /// <remarks>
    /// Two articles called "Deploying" is an ordinary thing for a firm to have — one about the
    /// pipeline and one about doing it by hand — and the second must not fail to save with a
    /// unique index violation somebody has to read a stack trace to understand. A suffix is
    /// appended rather than the title being refused, because the title is the author's business
    /// and the address is the system's.
    ///
    /// It reads the addresses already taken rather than asking once per candidate, so this
    /// costs one query whatever the answer. The bound exists because a loop without one is a
    /// loop: at a hundred articles sharing a title something else has gone wrong, and saying so
    /// is more use than another suffix.
    /// </remarks>
    private async Task<Slug> AnAddressNothingElseHas(
        string title, CancellationToken cancellationToken)
    {
        var basis = Slug.From(title);
        var taken = await articles.KeysLikeAsync(basis.Value, cancellationToken);

        if (!taken.Contains(basis.Value, StringComparer.Ordinal))
        {
            return basis;
        }

        for (var next = 2; next <= Most; next++)
        {
            var candidate = $"{basis.Value}-{next}";

            if (!taken.Contains(candidate, StringComparer.Ordinal))
            {
                return Slug.FromStored(candidate);
            }
        }

        throw new InvalidOperationException(
            $"There are already {Most} articles whose address begins '{basis.Value}'. Give this "
            + "one a title that says what makes it different.");
    }

    private async Task<Article> Required(Guid id, CancellationToken cancellationToken) =>
        await articles.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That article is not on file.");
}

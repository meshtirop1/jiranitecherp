using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Assets;

namespace JiranisokoTech.Application.Assets;

using Money = JiranisokoTech.Domain.Common.Money;

/// <summary>What the asset register needs read and written.</summary>
public interface IAssetRepository
{
    Task<Asset?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Asset?> ByTagAsync(string tag, CancellationToken cancellationToken = default);

    Task<bool> TagTakenAsync(
        string tag, Guid? except = null, CancellationToken cancellationToken = default);

    /// <summary>Everything, newest first.</summary>
    Task<List<Asset>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>Whatever this person is holding.</summary>
    /// <remarks>
    /// The query both the joiner's checklist and the leaver's list are built on, which is the
    /// point of section 15: those were two separate lists that could not agree, and are now two
    /// views of this.
    /// </remarks>
    Task<List<Asset>> HeldByAsync(
        Guid personId, CancellationToken cancellationToken = default);

    /// <summary>Everything nobody is holding, oldest purchase first.</summary>
    Task<List<Asset>> InStockAsync(CancellationToken cancellationToken = default);

    void Add(Asset asset);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The register of what the firm owns, and where each thing is.
/// </summary>
/// <remarks>
/// Section 15. Everything here is a record of something a person did — nothing scans a network,
/// reads a device management console or discovers anything on its own, and a register that
/// claimed to would be one nobody checks.
///
/// <b>Issuing and taking back are the two operations that matter</b>, because they are the ones
/// the rest of the system reaches for: the joiner's checklist issues, the leaver's list takes
/// back, and both now write to the same row rather than to lists of their own.
/// </remarks>
public sealed class AssetService(IAssetRepository assets, IClock clock)
{
    /// <summary>
    /// Record something the firm has bought.
    /// </summary>
    /// <remarks>
    /// The tag is checked here as well as by the index, because the index's message is a
    /// constraint violation and this one can say which asset already has it — and somebody
    /// typing a tag that exists has nearly always picked up the wrong machine.
    /// </remarks>
    public async Task<Asset> BuyAsync(
        string tag,
        AssetKind kind,
        string description,
        DateOnly boughtOn,
        string? serialNumber = null,
        Money? cost = null,
        CancellationToken cancellationToken = default)
    {
        var label = (tag ?? string.Empty).Trim().ToUpperInvariant();

        if (await assets.TagTakenAsync(label, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException(
                $"{label} is already on the register. Tags are the firm's own labels and no two "
                + "things share one — check the sticker.");
        }

        var asset = Asset.Buy(
            label, kind, description, boughtOn, clock.Now, serialNumber, cost);

        assets.Add(asset);
        await assets.SaveAsync(cancellationToken);

        return asset;
    }

    public async Task DescribeAsync(
        Guid id,
        AssetKind kind,
        string description,
        string? serialNumber,
        CancellationToken cancellationToken = default)
    {
        var asset = await Required(id, cancellationToken);

        asset.Describe(kind, description, serialNumber);

        await assets.SaveAsync(cancellationToken);
    }

    public async Task PaperworkAsync(
        Guid id,
        Money? cost,
        DateOnly boughtOn,
        DateOnly? warrantyEndsOn,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var asset = await Required(id, cancellationToken);

        asset.Paperwork(cost, boughtOn, warrantyEndsOn, notes);

        await assets.SaveAsync(cancellationToken);
    }

    public async Task IssueAsync(
        Guid id,
        Guid personId,
        DateOnly on,
        string? why = null,
        CancellationToken cancellationToken = default)
    {
        var asset = await Required(id, cancellationToken);

        asset.Issue(personId, on, clock.Now, why);

        await assets.SaveAsync(cancellationToken);
    }

    public async Task TakeBackAsync(
        Guid id,
        DateOnly on,
        string? condition = null,
        CancellationToken cancellationToken = default)
    {
        var asset = await Required(id, cancellationToken);

        asset.TakeBack(on, clock.Now, condition);

        await assets.SaveAsync(cancellationToken);
    }

    public async Task RepairAsync(
        Guid id, string why, DateOnly on, CancellationToken cancellationToken = default)
    {
        var asset = await Required(id, cancellationToken);

        asset.SendForRepair(why, on, clock.Now);

        await assets.SaveAsync(cancellationToken);
    }

    public async Task RepairedAsync(
        Guid id, string what, DateOnly on, CancellationToken cancellationToken = default)
    {
        var asset = await Required(id, cancellationToken);

        asset.BackFromRepair(what, on, clock.Now);

        await assets.SaveAsync(cancellationToken);
    }

    public async Task RetireAsync(
        Guid id, string why, DateOnly on, CancellationToken cancellationToken = default)
    {
        var asset = await Required(id, cancellationToken);

        asset.Retire(why, on, clock.Now);

        await assets.SaveAsync(cancellationToken);
    }

    public async Task MissingAsync(
        Guid id, string why, DateOnly on, CancellationToken cancellationToken = default)
    {
        var asset = await Required(id, cancellationToken);

        asset.Missing(why, on, clock.Now);

        await assets.SaveAsync(cancellationToken);
    }

    public async Task FoundAsync(
        Guid id, DateOnly on, CancellationToken cancellationToken = default)
    {
        var asset = await Required(id, cancellationToken);

        asset.Found(on, clock.Now);

        await assets.SaveAsync(cancellationToken);
    }

    public Task<Asset?> OneAsync(Guid id, CancellationToken cancellationToken = default) =>
        assets.FindAsync(id, cancellationToken);

    public Task<List<Asset>> AllAsync(CancellationToken cancellationToken = default) =>
        assets.AllAsync(cancellationToken);

    /// <summary>What this person is holding, for their checklist at either end.</summary>
    public Task<List<Asset>> HeldByAsync(
        Guid personId, CancellationToken cancellationToken = default) =>
        assets.HeldByAsync(personId, cancellationToken);

    /// <summary>What can be handed out, for the joiner's checklist to pick from.</summary>
    public Task<List<Asset>> InStockAsync(CancellationToken cancellationToken = default) =>
        assets.InStockAsync(cancellationToken);

    private async Task<Asset> Required(Guid id, CancellationToken cancellationToken) =>
        await assets.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That is not on the register.");
}

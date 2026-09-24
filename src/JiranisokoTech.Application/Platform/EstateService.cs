using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Platform;

namespace JiranisokoTech.Application.Platform;

/// <summary>What the service catalogue and the resource register need read and written.</summary>
public interface IEstateRepository
{
    Task<Service?> FindServiceAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<Service>> ServicesAsync(CancellationToken cancellationToken = default);

    Task<bool> ServiceNamedAsync(
        string name, Guid? except = null, CancellationToken cancellationToken = default);

    Task<Resource?> FindResourceAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<Resource>> ResourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>What belongs to one service.</summary>
    Task<List<Resource>> ResourcesForAsync(
        Guid serviceId, CancellationToken cancellationToken = default);

    void Add(Service service);

    void Add(Resource resource);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What the firm runs, and what it runs on.
/// </summary>
/// <remarks>
/// Section 14, and it is deliberately a register rather than a control plane. Nothing here
/// provisions, restarts, scales or checks anything: every row is something a person typed, and
/// the screens say so. A catalogue that claimed to read a cloud account would be believed, and
/// would go quietly wrong the first time a credential expired — the worst failure available to
/// a record of what exists.
///
/// What it is for is two questions. "The despatch board is down — who owns it, what does it run
/// on, what repository is it built from" is the one an incident asks. "What expires in the next
/// two months" is the one nobody asks until the certificate has already gone, which is why that
/// one is a job rather than a screen.
/// </remarks>
public sealed class EstateService(IEstateRepository estate, IClock clock)
{
    /// <summary>
    /// Put a service in the catalogue.
    /// </summary>
    /// <remarks>
    /// The name is checked for a duplicate, because two entries called "despatch board" means
    /// every incident after that is filed against whichever one the person picked, and the
    /// history splits in half without anybody noticing.
    /// </remarks>
    public async Task<Service> AddServiceAsync(
        string name,
        string description,
        HowCritical matters,
        Guid? ownerId = null,
        Guid? repositoryId = null,
        CancellationToken cancellationToken = default)
    {
        if (await estate.ServiceNamedAsync(name, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException(
                $"There is already a service called {name.Trim()}. Two with one name means "
                + "every incident after this is filed against whichever one somebody picked.");
        }

        var service = Service.Add(
            name, description, matters, clock.Now, ownerId, repositoryId);

        estate.Add(service);
        await estate.SaveAsync(cancellationToken);

        return service;
    }

    public async Task DescribeServiceAsync(
        Guid id,
        string name,
        string description,
        HowCritical matters,
        Guid? ownerId,
        Guid? repositoryId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var service = await RequiredService(id, cancellationToken);

        if (await estate.ServiceNamedAsync(name, id, cancellationToken))
        {
            throw new InvalidOperationException(
                $"There is already another service called {name.Trim()}.");
        }

        service.Describe(name, description, matters, ownerId, repositoryId, notes);

        await estate.SaveAsync(cancellationToken);
    }

    public async Task RetireServiceAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var service = await RequiredService(id, cancellationToken);

        service.Retire(clock.Now);

        await estate.SaveAsync(cancellationToken);
    }

    public async Task StillRunningAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var service = await RequiredService(id, cancellationToken);

        service.StillRunning();

        await estate.SaveAsync(cancellationToken);
    }

    public Task<List<Service>> ServicesAsync(CancellationToken cancellationToken = default) =>
        estate.ServicesAsync(cancellationToken);

    public Task<Service?> ServiceAsync(Guid id, CancellationToken cancellationToken = default) =>
        estate.FindServiceAsync(id, cancellationToken);

    public async Task<Resource> RecordAsync(
        string name,
        ResourceKind kind,
        DeploymentEnvironment environment,
        string? provider = null,
        Guid? serviceId = null,
        DateOnly? expiresOn = null,
        CancellationToken cancellationToken = default)
    {
        var resource = Resource.Record(
            name, kind, environment, clock.Now, provider, serviceId, expiresOn);

        estate.Add(resource);
        await estate.SaveAsync(cancellationToken);

        return resource;
    }

    public async Task DescribeResourceAsync(
        Guid id,
        string name,
        ResourceKind kind,
        string? provider,
        DeploymentEnvironment environment,
        Guid? serviceId,
        DateOnly? expiresOn,
        string? address,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var resource = await RequiredResource(id, cancellationToken);

        resource.Describe(
            name, kind, provider, environment, serviceId, expiresOn, address, notes);

        await estate.SaveAsync(cancellationToken);
    }

    public async Task RetireResourceAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var resource = await RequiredResource(id, cancellationToken);

        resource.Retire(clock.Now);

        await estate.SaveAsync(cancellationToken);
    }

    public async Task StillThereAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var resource = await RequiredResource(id, cancellationToken);

        resource.StillThere();

        await estate.SaveAsync(cancellationToken);
    }

    public Task<List<Resource>> ResourcesAsync(CancellationToken cancellationToken = default) =>
        estate.ResourcesAsync(cancellationToken);

    public Task<Resource?> ResourceAsync(Guid id, CancellationToken cancellationToken = default) =>
        estate.FindResourceAsync(id, cancellationToken);

    public Task<List<Resource>> ResourcesForAsync(
        Guid serviceId, CancellationToken cancellationToken = default) =>
        estate.ResourcesForAsync(serviceId, cancellationToken);

    private async Task<Service> RequiredService(
        Guid id, CancellationToken cancellationToken) =>
        await estate.FindServiceAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That service is not in the catalogue.");

    private async Task<Resource> RequiredResource(
        Guid id, CancellationToken cancellationToken) =>
        await estate.FindResourceAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That is not on the register.");
}

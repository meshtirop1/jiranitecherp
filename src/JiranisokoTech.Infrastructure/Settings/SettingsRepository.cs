using JiranisokoTech.Application.Settings;
using JiranisokoTech.Domain.Settings;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Settings;

public sealed class SettingsRepository(AppDbContext database) : ISettingsRepository
{
    public Task<FirmSettings?> FindAsync(CancellationToken cancellationToken = default) =>
        database.Settings.FirstOrDefaultAsync(
            settings => settings.Id == FirmSettings.TheOnlyOne, cancellationToken);

    public Task<bool> AnyInvoicesAsync(CancellationToken cancellationToken = default) =>
        database.Invoices.AnyAsync(cancellationToken);

    public void Add(FirmSettings settings) => database.Settings.Add(settings);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}

using System.Reflection;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The database, chosen by what the connection string actually is.
    /// </summary>
    /// <remarks>
    /// PostgreSQL in production; SQLite when the connection string names a file,
    /// which is what lets the application run on a laptop with nothing installed
    /// and the suite run without a container. The schema is written portably so
    /// the difference stays a deployment detail rather than a fork in the code.
    /// </remarks>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Default")
            ?? "Data Source=jiranisokotech.db";

        services.AddDbContext<AppDbContext>(options =>
        {
            if (LooksLikeSqlite(connection))
            {
                options.UseSqlite(connection);
            }
            else
            {
                options.UseNpgsql(connection);
            }
        });

        services.AddSingleton<IClock, SystemClock>();

        return services;
    }

    /// <summary>
    /// The outbox dispatcher, and the map from a stored name back to an event.
    /// </summary>
    /// <remarks>
    /// The registry is built here rather than resolved lazily, so that two
    /// events sharing a short name stop the process on the way up. Discovering
    /// that at dispatch time instead would mean the first wrong delivery is also
    /// the first anybody hears of it.
    /// </remarks>
    public static IServiceCollection AddMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] eventAssemblies)
    {
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.Section));

        var assemblies = eventAssemblies.Length > 0
            ? eventAssemblies
            : [typeof(IDomainEvent).Assembly];

        services.AddSingleton(DomainEventRegistry.Build(assemblies));

        services.AddScoped<OutboxDispatcher>();
        services.AddHostedService<OutboxProcessor>();

        return services;
    }

    private static bool LooksLikeSqlite(string connection) =>
        connection.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
        && !connection.Contains("Host=", StringComparison.OrdinalIgnoreCase);
}

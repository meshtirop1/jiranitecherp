using JiranisokoTech.Application.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Infrastructure.Ai;

public static class AiRegistration
{
    /// <summary>
    /// The language model, the lookups it may make, and the usage log.
    /// </summary>
    /// <remarks>
    /// Registered whether or not a key is configured. With no key the model says so and every
    /// screen shows it; registering nothing instead would make the pages fail to construct, which
    /// is an error screen where a sentence belongs.
    ///
    /// Its own method rather than more lines in AddModules, so that the whole feature can be read —
    /// or switched off — in one place.
    /// </remarks>
    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.Section));

        var baseAddress = configuration.GetSection(AiOptions.Section)
            .GetValue(nameof(AiOptions.BaseAddress), new AiOptions().BaseAddress)!;

        services.AddHttpClient(AnthropicModel.ClientName, client =>
        {
            client.BaseAddress = new Uri(baseAddress.EndsWith('/') ? baseAddress : baseAddress + "/");

            // The deadline is the model's own, across its retries; this is only the backstop for a
            // connection that never answers at all.
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JiranisokoTech-Delivery/1.0");
        });

        services.AddScoped<IAiModel, AnthropicModel>();
        services.AddScoped<AiLedger>();
        services.AddScoped<ProjectFacts>();
        services.AddScoped<AssistantTools>();
        services.AddScoped<Assistant>();
        services.AddScoped<ProjectReading>();
        services.AddScoped<CvReader>();
        services.AddScoped<RecruitingAid>();

        return services;
    }
}

using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure;
using JiranisokoTech.Web.Components;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Time as a dependency, so rules about probation ending, invoices falling
// overdue and certificates expiring can be tested on a day that is not today.
builder.Services.AddSingleton<IClock, SystemClock>();

/*
 * Two health endpoints, answering two different questions.
 *
 * /health  — is this process alive? If it fails, the orchestrator restarts the
 *            container. It must therefore check nothing external: a database
 *            outage that restarts every application instance in a loop turns a
 *            recoverable incident into an outage.
 *
 * /ready   — can this instance serve a request? It checks dependencies, and a
 *            failure takes the instance out of the load balancer without
 *            killing it, so it can come back when the database does.
 *
 * Conflating the two is the classic way to convert a slow database into a
 * crash loop.
 */
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

builder.Services.AddOpenApi();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

/*
 * TLS is terminated by the reverse proxy in front of the container, so the app
 * speaks plain HTTP on 8080 and redirecting inside it would loop. In
 * development there is no proxy, so the redirect is wanted.
 */
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
});

app.MapHealthChecks("/ready");

if (app.Environment.IsDevelopment())
{
    // The document describes the API; it is not published to the internet.
    app.MapOpenApi();
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Named so the test project can drive the real application through
/// WebApplicationFactory rather than a stand-in that drifts from it.
/// </summary>
public partial class Program;

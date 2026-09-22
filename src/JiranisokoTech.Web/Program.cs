using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Web.Authorization;
using JiranisokoTech.Web.Components;
using JiranisokoTech.Web.Identity;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The database, the clock, and the stores accounts are kept in.
builder.Services.AddPersistence(builder.Configuration);

// The outbox dispatcher. Events raised inside a transaction are published from
// here after it commits, which is the only way the two can be made to agree.
builder.Services.AddMessaging(builder.Configuration);

// Accounts and sign-in, then authorization. Registered in that order because
// the authorization fallback below assumes authentication exists.
builder.Services.AddApplicationIdentity();
builder.Services.AddPermissionAuthorization();

// Makes who is signed in available to components as a cascading value. Without
// it AuthorizeView renders nothing at all — silently, which is the failure mode
// that gets shipped.
builder.Services.AddCascadingAuthenticationState();

// Who is acting, read from the request. This is what makes the audit trail
// name people instead of recording a null on every entry.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

builder.Services.AddScoped<RoleSeeder>();
builder.Services.AddScoped<OwnerSeeder>();

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
    app.UseExceptionHandler("/error", createScopeForErrors: true);
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

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

/*
 * Anonymous, deliberately, and the only two endpoints that are.
 *
 * Everything else is closed by the fallback policy — a page added without an
 * attribute is protected rather than public, because the opposite default
 * fails silently and nobody finds out until it matters. But an orchestrator
 * cannot sign in, and a liveness probe that returns 302 to a login page reads
 * as an unhealthy container and restarts it forever.
 *
 * Neither endpoint reveals anything: they answer "Healthy" or a status code.
 */
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
}).AllowAnonymous();

app.MapHealthChecks("/ready").AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    // The document describes the API; it is not published to the internet.
    app.MapOpenApi();
}

/*
 * The role matrix is applied on every start, not once at install.
 *
 * A permission added in code changes what the application checks, while a role
 * keeps whatever it was created with — so without this, adding a permission
 * grants it to nobody and the feature refuses its own users by name. Doing it
 * here means the database cannot be out of step with the code that reads it.
 */
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await database.Database.EnsureCreatedAsync();

    await scope.ServiceProvider.GetRequiredService<RoleSeeder>().SeedAsync();

    // And the first account, if this installation has none and one is
    // configured. After that it is a no-op on every start.
    await scope.ServiceProvider.GetRequiredService<OwnerSeeder>().SeedAsync();
}

// Ending a session. A POST, so it cannot be triggered by a link.
app.MapAuthenticationEndpoints();

/*
 * Anonymous, because a stylesheet has no account.
 *
 * Without this the deny-by-default policy catches every css, js and font file
 * and answers each one with a redirect to the sign-in page — so the browser
 * receives HTML where it asked for CSS, discards it, and renders the login form
 * with no styling at all. The page is the first thing anybody ever sees of this
 * system, the failure is invisible to any test that reads markup, and the
 * assets are public files that ship inside the container regardless.
 */
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Named so the test project can drive the real application through
/// WebApplicationFactory rather than a stand-in that drifts from it.
/// </summary>
public partial class Program;

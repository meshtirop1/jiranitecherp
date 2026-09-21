using JiranisokoTech.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// The real application, against a database that exists for one test class.
/// </summary>
/// <remarks>
/// The whole pipeline runs: authentication, the deny-by-default policy,
/// antiforgery, the components. Sign-in is one of the things most often proved
/// against a stand-in and broken in production, because what breaks is rarely
/// the password check — it is a missing token, a cookie that is never written,
/// a policy that refuses the page doing the signing in. None of that exists in
/// a stand-in.
///
/// The connection string names an in-memory SQLite database with a shared
/// cache, and <see cref="_pin"/> holds a connection to it open for the lifetime
/// of the factory. Such a database exists only while some connection does, so
/// without the pin the schema would vanish between two EF operations.
/// </remarks>
public class ApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString =
        $"Data Source=tests-{Guid.CreateVersion7():N};Mode=Memory;Cache=Shared";

    private SqliteConnection? _pin;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _pin = new SqliteConnection(_connectionString);
        _pin.Open();

        builder.UseSetting("ConnectionStrings:Default", _connectionString);
    }

    /// <summary>
    /// A client that talks https, because the session cookie is marked secure.
    /// </summary>
    /// <remarks>
    /// .NET's cookie container will accept a secure cookie over http and then
    /// never send it back, so a plain-http client signs in successfully and
    /// appears to be anonymous on the very next request — which looks exactly
    /// like broken sign-in and is not. There is no TLS involved either way; the
    /// scheme is what the cookie rules read.
    /// </remarks>
    public HttpClient CreateBrowser(bool followRedirects = false) =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = followRedirects,
        });

    public async Task<ApplicationUser> CreateAccountAsync(
        string email,
        string password,
        string displayName = "Test Person",
        bool active = true)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser(email, displayName)
        {
            // Confirmed, because sign-in requires it. A test that skipped this
            // would be testing the confirmation rule by accident and reporting
            // it as a password failure.
            EmailConfirmed = true,
            IsActive = active,
        };

        var result = await users.CreateAsync(user, password);

        Assert.True(
            result.Succeeded,
            "Could not create the test account: "
            + string.Join("; ", result.Errors.Select(error => error.Description)));

        return user;
    }

    /// <summary>
    /// Run something as though a request were in progress.
    /// </summary>
    /// <remarks>
    /// Signing in writes to a response, so the sign-in manager refuses to work
    /// without an <see cref="HttpContext"/>. This supplies a real one that is
    /// not attached to a socket, which is enough for everything except reading
    /// the bytes back — and the page tests cover that end.
    /// </remarks>
    public async Task<T> InRequestAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        return await work(scope.ServiceProvider);
    }

    public async Task InScopeAsync(Func<IServiceProvider, Task> work)
    {
        using var scope = Services.CreateScope();
        await work(scope.ServiceProvider);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _pin?.Dispose();
        }
    }
}

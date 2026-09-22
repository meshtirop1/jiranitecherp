using Microsoft.AspNetCore.DataProtection;

namespace JiranisokoTech.Web;

/// <summary>
/// Where the keys that sign cookies are kept.
/// </summary>
/// <remarks>
/// Data Protection generates a key ring and uses it to sign and encrypt the
/// authentication cookie, the antiforgery token and the password-reset links
/// this application sends. Left alone it writes that key ring somewhere
/// convenient — on a container, inside the container's own filesystem, which
/// exists until the container is replaced.
///
/// The consequences are quiet rather than dramatic, which is why this is easy
/// to ship. A deploy replaces the container, the key ring is new, and every
/// cookie signed by the old one is unreadable: everybody is signed out. Every
/// password-reset link already in somebody's inbox stops working. And the
/// moment there is more than one container, the two disagree about every
/// cookie, so a person is signed in or out depending on which one answers.
///
/// So the keys live on a path that can be given a volume. The startup log says
/// which, because a value silently defaulting to a directory that disappears is
/// the whole failure being described here.
/// </remarks>
public static class SigningKeys
{
    public static void AddSigningKeyRing(this WebApplicationBuilder builder)
    {
        var path = builder.Configuration["DataProtection:KeyRingPath"];

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = new DirectoryInfo(path);
        directory.Create();

        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(directory)

            // Named rather than derived from the assembly's location, which is
            // what the default does. Two containers of the same application
            // must agree on this string or they cannot read each other's
            // cookies, and the path inside a container is not a promise.
            .SetApplicationName("JiranisokoTech.Delivery");
    }

    /// <summary>
    /// Said once at startup, whichever way it was configured.
    /// </summary>
    /// <remarks>
    /// Separate from the registration above because it needs a logger, and the
    /// logger does not exist until the application is built.
    /// </remarks>
    public static void ReportSigningKeyRing(this WebApplication app)
    {
        var path = app.Configuration["DataProtection:KeyRingPath"];

        if (string.IsNullOrWhiteSpace(path))
        {
            app.Logger.LogWarning(
                "Signing keys are not being persisted. They will be regenerated when this "
                + "process is replaced, which signs everybody out and invalidates any "
                + "password-reset link already sent. Set DataProtection__KeyRingPath to a "
                + "directory that outlives the container.");

            return;
        }

        app.Logger.LogInformation("Signing keys are kept in {Path}.", path);
    }
}

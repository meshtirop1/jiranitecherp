namespace JiranisokoTech.Application.Authorization;

/// <summary>
/// How a permission travels on a signed-in principal.
/// </summary>
/// <remarks>
/// Declared once, in the layer both halves reference. The seeder writes these
/// claims and the authorization handler reads them, and they live in different
/// projects — two copies of this string would compile perfectly and fail only
/// at runtime, as silently as it is possible to fail.
/// </remarks>
public static class PermissionClaim
{
    public const string Type = "permission";

    /// <summary>Marks a policy name as a permission check: <c>permission:tasks.create</c>.</summary>
    public const string PolicyPrefix = "permission:";
}

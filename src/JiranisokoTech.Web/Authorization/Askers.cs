using System.Security.Claims;
using JiranisokoTech.Application.Ai;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Infrastructure.People;

namespace JiranisokoTech.Web.Authorization;

/// <summary>
/// Who is asking, read from the signed-in principal.
/// </summary>
/// <remarks>
/// The same claims every authorization check reads, and the same staff-record lookup the search
/// page makes — so an AI lookup refuses exactly what the page for the same records would. One
/// method, because a second copy in each AI page is where one of them would come to read the
/// permissions from somewhere else.
/// </remarks>
public static class Askers
{
    public static async Task<Asker> FromAsync(
        ClaimsPrincipal user, PeopleQueries people, CancellationToken cancellationToken = default)
    {
        Guid? account = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

        return new Asker(
            account,
            user.FindFirstValue("display_name") ?? user.Identity?.Name,
            user.FindAll(PermissionClaim.Type).Select(claim => claim.Value).ToHashSet(),
            account is { } known ? await people.EmployeeForAccountAsync(known, cancellationToken) : null);
    }
}

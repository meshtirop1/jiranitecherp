using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Authorization;

/// <summary>
/// Everybody who works here can do the things everybody who works here does.
/// </summary>
/// <remarks>
/// <b>This exists because <c>SelfService</c> stopped reaching four of the roles that need it,
/// and nothing said so.</b>
///
/// The array was extracted with a comment explaining itself: logging hours, asking for leave,
/// claiming money back and asking the firm to buy something are not privileges, and a role
/// without them describes somebody who does not work here. Roles added after the first seven
/// spread it. The first seven were left listing those permissions by hand, and the comment on
/// the array recorded that as deliberate — "which each repeat it in full with the reasoning
/// beside it".
///
/// Which was true on the day it was written. Then the array grew twice. Section 25 added
/// <c>knowledge.write</c>, on the argument that writing down how a thing is done is not a
/// privilege; section 26 added <c>support.view</c> and <c>support.ask</c>, on the argument that
/// a record fewer people can see is one people ask about in a corridor instead. Neither reached
/// the four hand-written roles, so a developer, a project manager, a department head and
/// whoever runs HR could not write an article, could not read the help desk, and could not ask
/// for help.
///
/// The worst of it was the project manager, who had been granted <c>support.run</c> in the same
/// change: the authority to answer a client on a ticket they could not open. Every test passed.
/// EnforcementTests could not see it — the permissions ARE checked, on pages that exist.
/// PermissionTests could not see it — every grant present was legitimate. What was wrong was an
/// absence, and an absence in a list nobody diffs.
/// </remarks>
public class SelfServiceTests
{
    /// <summary>
    /// Roles that deliberately hold less than this, with the reason.
    /// </summary>
    /// <remarks>
    /// Both entries describe somebody who is not on the payroll, which is the only reason that
    /// works here. A role reaches this list by being genuinely somebody else's, and never by
    /// being inconvenient to grant.
    /// </remarks>
    private static readonly Dictionary<string, string> NotOnThePayroll = new()
    {
        [Roles.Auditor] =
            "An auditor reads and writes nothing — see An_auditor_only_reads, which fails the "
            + "build for any grant on this role that is not a reading shape. Most of "
            + "SelfService is an action, so the role cannot hold it and stay what it is. It is "
            + "also frequently somebody from outside the firm, with no hours to log here.",

        [Roles.Interviewer] =
            "Worn alongside another role, which carries the self-service set — the role's own "
            + "comment says so. It exists to open one candidate and score one interview, and "
            + "nobody holds it by itself.",
    };

    /// <summary>
    /// A role is never handed the same permission twice.
    /// </summary>
    /// <remarks>
    /// The other side of the fix above. Spreading <c>SelfService</c> into a role that also names
    /// one of those permissions on a line of its own is deliberate — both sentences are worth
    /// keeping — so the duplication is real and has to be removed on the way out. It matters in
    /// exactly one place: <c>RoleSeeder</c> turns this list into role claims, and the same
    /// permission twice is at best two identical rows and at worst a unique index refusing the
    /// seed while the application is starting.
    /// </remarks>
    [Fact]
    public void A_role_is_never_handed_the_same_permission_twice()
    {
        var doubled = new List<string>();

        foreach (var role in Roles.All)
        {
            var held = Roles.PermissionsFor(role);
            var said = held.GroupBy(one => one, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            if (said.Count > 0)
            {
                doubled.Add($"  {role}: {string.Join(", ", said)}");
            }
        }

        Assert.True(
            doubled.Count == 0,
            "PermissionsFor hands these roles the same permission more than once:\n"
            + string.Join('\n', doubled.Order()));
    }

    /// <summary>
    /// A role either holds all of it or is named as an exception.
    /// </summary>
    /// <remarks>
    /// All of it, rather than "as much as the role happened to be given". Partial is the state
    /// the four hand-written roles were in, and partial is indistinguishable from deliberate
    /// when you are reading one role's list rather than diffing it against an array eight
    /// hundred lines away.
    /// </remarks>
    [Fact]
    public void Every_role_that_works_here_can_do_what_everybody_does()
    {
        var short_of = new List<string>();

        foreach (var (role, held) in Roles.Matrix)
        {
            if (NotOnThePayroll.ContainsKey(role))
            {
                continue;
            }

            var missing = Roles.SelfService.Where(one => !held.Contains(one)).ToList();

            if (missing.Count > 0)
            {
                short_of.Add($"  {role} is missing {string.Join(", ", missing)}");
            }
        }

        Assert.True(
            short_of.Count == 0,
            "These roles describe somebody who works here and cannot do something everybody "
            + "who works here does:\n"
            + string.Join('\n', short_of.Order())
            + "\n\nSpread `.. SelfService` into the role rather than listing the permissions "
            + "again, so that the next thing added to the array reaches it. If the role really "
            + "should hold less, put it in NotOnThePayroll with a reason that is a fact about "
            + "the person rather than about the wiring.");
    }

    /// <summary>
    /// Nothing in the exemption list has stopped being a role.
    /// </summary>
    /// <remarks>
    /// An exemption outliving the thing it exempts is how a list like this turns into a place
    /// where things go to be forgotten — the same guard ReachabilityTests carries on its own.
    /// </remarks>
    [Fact]
    public void The_exemption_list_has_nothing_stale_in_it()
    {
        var stale = NotOnThePayroll.Keys
            .Where(role => !Roles.Matrix.ContainsKey(role))
            .ToList();

        Assert.True(
            stale.Count == 0,
            "These are exempted from the self-service check and are no longer roles: "
            + string.Join(", ", stale));
    }

    /// <summary>
    /// Nothing in the self-service set is an authority over somebody else.
    /// </summary>
    /// <remarks>
    /// The other direction, and the one that would make this test dangerous if it went
    /// unguarded. Everything in the array is granted to every role by construction, so anything
    /// added to it is granted to eighteen roles at once by whoever adds it — and the test above
    /// would then insist on that. A permission ending <c>.view_all</c>, <c>.run</c>,
    /// <c>.decide</c>, <c>.approve</c>, <c>.manage</c> or <c>.set</c> reaches beyond the person
    /// holding it, so it does not belong in a list about doing things for yourself.
    /// </remarks>
    [Fact]
    public void Nothing_in_the_self_service_set_reaches_past_the_person_holding_it()
    {
        string[] overOthers =
            [".view_all", ".run", ".decide", ".approve", ".manage", ".set", ".delete"];

        var reaching = Roles.SelfService
            .Where(one => overOthers.Any(
                ending => one.EndsWith(ending, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            reaching.Count == 0,
            "These are in the set granted to every role, and each is an authority over "
            + "somebody other than the holder: " + string.Join(", ", reaching)
            + "\n\nGrant it to the roles that should have it instead.");
    }
}

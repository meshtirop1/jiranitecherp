using System.Security.Claims;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Authorization;

/// <summary>
/// The matrix, and the machinery that enforces it.
/// </summary>
public class PermissionTests
{
    /// <summary>
    /// A role granting something nobody declared is a permission with no
    /// source — impossible to audit, and impossible to find when somebody asks
    /// where an access came from.
    /// </summary>
    [Fact]
    public void Every_granted_permission_is_a_declared_one()
    {
        foreach (var (role, permissions) in Roles.Matrix)
        {
            foreach (var permission in permissions)
            {
                Assert.True(
                    Permissions.All.Contains(permission),
                    $"Role '{role}' grants '{permission}', which is not declared in Permissions.All.");
            }
        }
    }

    /// <summary>
    /// The reverse: a permission the code checks but no role holds refuses
    /// everybody, silently, and looks exactly like a broken feature.
    /// </summary>
    [Fact]
    public void Every_declared_permission_is_held_by_somebody()
    {
        var granted = Roles.Matrix.Values.SelectMany(p => p).ToHashSet();

        var orphaned = Permissions.All.Where(p => !granted.Contains(p)).ToList();

        Assert.True(
            orphaned.Count == 0,
            "Declared but granted to no role: " + string.Join(", ", orphaned));
    }

    [Fact]
    public void Permissions_are_named_resource_dot_action()
    {
        foreach (var permission in Permissions.All)
        {
            var parts = permission.Split('.');

            Assert.True(parts.Length == 2, $"'{permission}' is not resource.action.");
            Assert.All(parts, part => Assert.False(string.IsNullOrWhiteSpace(part)));
            Assert.Equal(permission.ToLowerInvariant(), permission);
        }
    }

    [Fact]
    public void Nothing_is_declared_twice()
    {
        Assert.Equal(Permissions.All.Count, Permissions.All.Distinct().Count());
    }

    /// <summary>
    /// Somebody has to be able to grant roles, or the system cannot be
    /// administered once the first administrator leaves.
    /// </summary>
    [Fact]
    public void The_owner_can_manage_roles_and_holds_everything()
    {
        Assert.Contains(Permissions.RolesManage, Roles.PermissionsFor(Roles.Owner));
        Assert.Equal(Permissions.All.Count, Roles.PermissionsFor(Roles.Owner).Count);
    }

    /// <summary>
    /// A developer holds almost nothing, which is the case most likely to be
    /// broken by somebody adding a permission to the wrong list.
    /// </summary>
    [Fact]
    public void A_developer_cannot_administer_anything()
    {
        var developer = Roles.PermissionsFor(Roles.Developer);

        Assert.DoesNotContain(Permissions.UsersManage, developer);
        Assert.DoesNotContain(Permissions.UsersAssignRoles, developer);
        Assert.DoesNotContain(Permissions.RolesManage, developer);
        Assert.DoesNotContain(Permissions.ApprovalsDecide, developer);
        Assert.DoesNotContain(Permissions.AuditView, developer);
    }

    /// <summary>
    /// HR sits at step one of the hiring chain. Without this the chain opens on
    /// a step nobody in the firm can answer — a fault the system this replaces
    /// shipped with.
    /// </summary>
    [Fact]
    public void Hr_can_decide_an_approval()
    {
        Assert.Contains(Permissions.ApprovalsDecide, Roles.PermissionsFor(Roles.HumanResources));
    }

    /// <summary>
    /// Review without create means a head can only ever react to work somebody
    /// else set.
    /// </summary>
    [Fact]
    public void A_department_head_can_set_work_as_well_as_review_it()
    {
        var head = Roles.PermissionsFor(Roles.DepartmentHead);

        Assert.Contains(Permissions.TasksCreate, head);
        Assert.Contains(Permissions.TasksAssign, head);
        Assert.Contains(Permissions.TasksReview, head);
    }

    /// <summary>
    /// Every role a person actually holds lets them log their own hours, ask for
    /// leave and claim money back.
    /// </summary>
    /// <remarks>
    /// The one lesson this system has learned twice. An engineer could not open
    /// the work board because no role held tasks.view_own; an interviewer could
    /// submit a scorecard for an interview they could not open. Both were the
    /// same mistake — a permission granted for doing a thing, with nothing
    /// granted for reaching it. Self-service is where that mistake is most
    /// expensive, because it locks out the majority of the staff.
    ///
    /// Interviewer is exempt: it is designed to be worn alongside another role,
    /// and that role carries these.
    /// </remarks>
    [Fact]
    public void Everybody_who_works_here_can_log_time_ask_for_leave_and_claim_expenses()
    {
        foreach (var role in Roles.All.Where(role => role != Roles.Interviewer))
        {
            var held = Roles.PermissionsFor(role);

            Assert.Contains(Permissions.TimeLogOwn, held);
            Assert.Contains(Permissions.LeaveAsk, held);
            Assert.Contains(Permissions.ExpensesClaim, held);
        }
    }

    /// <summary>
    /// Approving a claim and paying it are held by different people.
    /// </summary>
    /// <remarks>
    /// Except at the top, where somebody has to be able to do everything. Below
    /// that, one person holding both can approve their own reimbursement and
    /// record it as paid.
    /// </remarks>
    [Fact]
    public void Approving_an_expense_and_paying_it_are_not_the_same_hands()
    {
        var both = Roles.All
            .Where(role => role is not (Roles.Owner or Roles.Administrator))
            .Where(role => Roles.PermissionsFor(role).Contains(Permissions.ExpensesApprove)
                && Roles.PermissionsFor(role).Contains(Permissions.ExpensesPay))
            .ToList();

        Assert.Empty(both);
    }

    /// <summary>
    /// Drafting an invoice and sending it are likewise separate.
    /// </summary>
    [Fact]
    public void Drafting_an_invoice_and_sending_it_are_not_the_same_hands()
    {
        var both = Roles.All
            .Where(role => role is not (Roles.Owner or Roles.Administrator))
            .Where(role => Roles.PermissionsFor(role).Contains(Permissions.InvoicesManage)
                && Roles.PermissionsFor(role).Contains(Permissions.InvoicesSend))
            .ToList();

        Assert.Empty(both);
    }

    /// <summary>
    /// Anybody who can submit a scorecard can open the interview it is for.
    /// </summary>
    [Fact]
    public void A_scorecard_cannot_be_asked_for_without_a_way_to_see_the_interview()
    {
        foreach (var role in Roles.All)
        {
            var held = Roles.PermissionsFor(role);

            if (held.Contains(Permissions.ScorecardsSubmit))
            {
                Assert.Contains(Permissions.InterviewsView, held);
            }
        }
    }

    // --- the machinery -----------------------------------------------------

    [Fact]
    public async Task A_policy_is_built_for_any_declared_permission()
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var policy = await provider.GetPolicyAsync(
            PermissionClaim.PolicyPrefix + Permissions.TasksCreate);

        Assert.NotNull(policy);
        Assert.Single(policy!.Requirements.OfType<PermissionRequirement>());
        Assert.Equal(
            Permissions.TasksCreate,
            policy.Requirements.OfType<PermissionRequirement>().Single().Permission);
    }

    /// <summary>A policy name that is not a permission is left to the default provider.</summary>
    [Fact]
    public async Task An_unrelated_policy_name_is_not_claimed()
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        Assert.Null(await provider.GetPolicyAsync("something-else"));
    }

    [Fact]
    public async Task The_requirement_is_met_only_by_holding_the_permission()
    {
        var handler = new PermissionAuthorizationHandler();
        var requirement = new PermissionRequirement(Permissions.TasksReview);

        var holder = Principal(Permissions.TasksReview, Permissions.TasksCreate);
        var granted = new AuthorizationHandlerContext([requirement], holder, null);
        await handler.HandleAsync(granted);

        Assert.True(granted.HasSucceeded);

        var other = Principal(Permissions.TasksCreate);
        var refused = new AuthorizationHandlerContext([requirement], other, null);
        await handler.HandleAsync(refused);

        Assert.False(refused.HasSucceeded);
    }

    /// <summary>
    /// Permissions are compared exactly. A prefix match would make
    /// "tasks.review" satisfy "tasks.review_all" the day somebody adds one.
    /// </summary>
    [Fact]
    public async Task A_similar_permission_does_not_satisfy_the_requirement()
    {
        var handler = new PermissionAuthorizationHandler();
        var requirement = new PermissionRequirement("tasks.deploy");

        var context = new AuthorizationHandlerContext(
            [requirement], Principal("tasks.deploy_staging"), null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public void Reading_permissions_off_a_principal_returns_what_was_granted()
    {
        var principal = Principal(Permissions.TasksCreate, Permissions.ReportsView);

        Assert.True(principal.HasPermission(Permissions.TasksCreate));
        Assert.False(principal.HasPermission(Permissions.TasksReview));
        // Compared as a set: the claim order on a principal is not meaningful,
        // and asserting it would make this fail the day the order changed for
        // a reason nobody cares about.
        Assert.Equal(
            new HashSet<string> { Permissions.TasksCreate, Permissions.ReportsView },
            principal.Permissions());
    }

    private static ClaimsPrincipal Principal(params string[] permissions) =>
        new(new ClaimsIdentity(
            permissions.Select(p => new Claim(PermissionClaim.Type, p)),
            authenticationType: "Test"));
}

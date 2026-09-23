using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiranisokoTech.Web.Api;

/// <summary>
/// What another system may change in this one.
/// </summary>
/// <remarks>
/// The API was read-only, and the file that made it so said why: writing through an API
/// means every rule this system enforces has to be enforced twice, or the API has to go
/// through the same services the screens use and answer the same refusals in a shape a
/// machine can act on. It said neither was hard and both were work to be done against a
/// real requirement rather than guessed at.
///
/// This takes the second path, exactly. Every endpoint below calls the same service method
/// the screen calls, so there is no second copy of any rule — an invoice raised through the
/// API is refused for a former client for the same reason and by the same code as one
/// raised on a page. The only thing written here is the translation of a refusal into a
/// status code.
///
/// <b>Three endpoints, each with a caller anybody can name.</b> Not a write for every read:
/// an API surface is a contract, and the way to regret one is to publish endpoints nobody
/// asked for and then be unable to change them.
///
///   A client taken on — from whatever the firm uses to win work.
///   A piece of work raised — from a developer's own tooling, which is the brief's point.
///   A payment recorded — from a bank feed or a gateway, which is the one that saves real
///   time, because it is otherwise typed in from a statement once a week.
///
/// <b>A key acts for the firm, never as a person.</b> So every endpoint here needs a
/// permission that is about the firm's business rather than somebody's own: clients.manage,
/// tasks.create, invoices.manage. There is deliberately no endpoint for logging time,
/// because time.log_own means "your own" and a key has no own — an API that logged hours
/// against a named employee would be one machine account able to put billable time on
/// anybody's timesheet.
/// </remarks>
public static class PublicApiWrites
{
    public static IEndpointRouteBuilder MapPublicApiWrites(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName,
            })
            .RequireRateLimiting(ApiLimits.Policy);

        api.MapPost("/clients", async (
            [FromBody] TakeOnClient body,
            ClientService clients,
            CancellationToken cancellationToken) =>
            await AnsweringAsync(async () =>
            {
                var client = await clients.TakeOnAsync(
                    body.Name,
                    body.Code,
                    body.ContactName,
                    body.ContactEmail,
                    cancellationToken);

                /*
                 * 201 with the address of the thing made, which is the one part of REST
                 * worth being strict about: a caller that has just created something needs
                 * to be able to refer to it, and guessing the address from an identifier is
                 * how a caller ends up hard-coding a path this system later changes.
                 */
                return Results.Created(
                    $"/api/v1/clients/{client.Id}",
                    new { id = client.Id, name = client.Name, code = client.Code });
            }))
        .RequirePermission(Permissions.ClientsManage)
        .WithSummary("Take on a client.")
        .WithDescription(
            "Refused if the name or code is already in use, because a second record for one "
            + "client splits their invoices across two histories.");

        api.MapPost("/work", async (
            [FromBody] RaiseWork body,
            WorkService work,
            CancellationToken cancellationToken) =>
            await AnsweringAsync(async () =>
            {
                var item = await work.RaiseAsync(
                    body.Title,
                    raisedById: body.RaisedById ?? Guid.Empty,
                    projectId: body.ProjectId,
                    cancellationToken: cancellationToken);

                return Results.Created(
                    $"/api/v1/work/{item.Id}",
                    new { id = item.Id, number = item.Number, reference = item.Reference });
            }))
        .RequirePermission(Permissions.TasksCreate)
        .WithSummary("Raise a piece of work.")
        .WithDescription(
            "Returns the number as well as the identifier. The number is what goes in a "
            + "branch name, and it is the only thing that ties a commit back to this work.");

        api.MapPost("/invoices/{id:guid}/payments", async (
            Guid id,
            [FromBody] RecordPayment body,
            InvoiceService invoices,
            CancellationToken cancellationToken) =>
            await AnsweringAsync(async () =>
            {
                /*
                 * The currency comes from the invoice rather than from the caller, and that
                 * is the whole of this endpoint's care. A payment posted in the wrong
                 * currency against the right invoice is an amount that looks settled and is
                 * not, and Money would refuse the arithmetic — but only after the figure had
                 * been believed by whoever sent it.
                 */
                await invoices.RecordPaymentAsync(
                    id,
                    body.MinorUnits,
                    body.PaidOn,
                    body.Reference,
                    cancellationToken);

                return Results.NoContent();
            }))
        .RequirePermission(Permissions.InvoicesManage)
        .WithSummary("Record a payment against an invoice.")
        .WithDescription(
            "The amount is in minor units and the currency is the invoice's. Refused if it "
            + "would pay more than is outstanding.");

        return endpoints;
    }

    /// <summary>
    /// Run a write, and turn a refusal into a status a machine can act on.
    /// </summary>
    /// <remarks>
    /// The one thing this file adds beyond the services, and the distinction it draws is
    /// the useful one.
    ///
    /// A malformed request is 400: the caller sent something this endpoint cannot read, and
    /// retrying it unchanged will fail again.
    ///
    /// A refused request is 422. The request was understood and a rule said no — a former
    /// client, a duplicate code, a payment larger than the debt. That is not the caller
    /// having made a syntactic mistake, and telling them 400 would send them looking at
    /// their JSON. The service's own sentence is passed through, because those sentences say
    /// what to do differently and a generic message throws that away.
    ///
    /// Nothing here catches anything else. An unexpected exception is a fault in this
    /// system, and answering it with a tidy 422 would tell the caller their request was
    /// wrong when it was not — and would hide the fault from whoever has to fix it.
    /// </remarks>
    private static async Task<IResult> AnsweringAsync(Func<Task<IResult>> write)
    {
        try
        {
            return await write();
        }
        catch (ArgumentException refusal)
        {
            return Results.Json(
                new { error = refusal.Message, reason = "malformed" },
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException refusal)
        {
            return Results.Json(
                new { error = refusal.Message, reason = "refused" },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>A client to take on.</summary>
    public sealed record TakeOnClient(
        string Name, string? Code, string? ContactName, string? ContactEmail);

    /// <summary>
    /// A piece of work to raise.
    /// </summary>
    /// <remarks>
    /// RaisedById is optional and means "the firm" when absent, because a key is not a
    /// person. Naming an employee is allowed so that tooling acting on somebody's behalf can
    /// say so, and it is not required, because most callers have no staff identifier to
    /// give.
    /// </remarks>
    public sealed record RaiseWork(string Title, Guid? RaisedById, Guid? ProjectId);

    /// <summary>A payment to record. The currency is the invoice's.</summary>
    public sealed record RecordPayment(long MinorUnits, DateOnly PaidOn, string? Reference);
}

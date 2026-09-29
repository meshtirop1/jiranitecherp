namespace JiranisokoTech.Application.Ai;

/// <summary>
/// The person a question is being answered for, and everything that decides what they may see.
/// </summary>
/// <remarks>
/// Every lookup the assistant makes is made as this person, and this record is how. It carries
/// the same permission claims every page reads and the same staff record the reaches narrow by,
/// so a lookup given one of these can refuse exactly what the page showing the same records
/// would refuse — no more, because the page is the standard, and no less, because an assistant
/// that can see further than the person asking is a way round every permission in the system.
///
/// Built once per question from the signed-in principal, never from anything the model says.
/// The model chooses which lookups to ask for and with which arguments; who is asking is not an
/// argument.
/// </remarks>
public sealed record Asker(
    Guid? AccountId,
    string? Name,
    IReadOnlySet<string> Permissions,
    Guid? EmployeeId)
{
    public bool Holds(string permission) => Permissions.Contains(permission);
}

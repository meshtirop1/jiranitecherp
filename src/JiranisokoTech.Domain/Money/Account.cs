using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Money;

/// <summary>
/// Which side of the firm's money an account is about.
/// </summary>
/// <remarks>
/// Two, and only two. A full chart has assets, liabilities and equity as well, and those
/// three exist to make a balance sheet balance — which needs double entry, a journal, and a
/// posting rule for every document. This firm's question is narrower and honest: what did we
/// earn, what did we spend, and on what. Adding the other three without the machinery behind
/// them would be a chart of accounts that looks like bookkeeping and is not.
/// </remarks>
public enum AccountKind
{
    /// <summary>Money the firm earns.</summary>
    Income = 1,

    /// <summary>Money the firm spends.</summary>
    Expense = 2,
}

/// <summary>
/// One line of the chart of accounts.
/// </summary>
/// <remarks>
/// Section 18 had invoices, payments and expense claims and no way to say what any of it was
/// for. "We spent four million shillings last quarter" was answerable; "on what" was not,
/// because nothing classified a cost beyond the project it was charged to — and a project is
/// who the money was for, not what it was.
///
/// <b>No balance on this record, and no journal anywhere.</b> The deliberate decision of this
/// whole section. A stored balance is a number that can disagree with the documents it was
/// added up from, and the day it does, nobody can tell which is wrong. Every figure in the
/// financial report is summed from the invoices, claims and charges themselves, so there is
/// exactly one version of what the firm earned.
///
/// <b>Retired, never deleted.</b> An account with history behind it cannot be removed without
/// making last year's report unreadable, and an account nobody may code new money to is what
/// people actually mean when they say they want to delete one.
/// </remarks>
public sealed class Account : Entity, IAuditable
{
    public const int MaximumCodeLength = 20;

    private Account()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    private Account(string code, string name, AccountKind kind)
    {
        Code = Coded(code);
        Name = Require(name, nameof(name));
        Kind = kind;
    }

    public static Account Open(string code, string name, AccountKind kind) =>
        new(code, name, kind);

    /// <summary>
    /// What people call it out loud, and what they type.
    /// </summary>
    /// <remarks>
    /// Upper-cased and trimmed, because a code is a key people type from memory and "4200"
    /// and " 4200 " are the same account to everybody except a string comparison. Free text
    /// rather than a fixed numbering scheme: every firm has one it already uses, and imposing
    /// one here would mean the codes in this system disagreeing with the codes on the
    /// accountant's spreadsheet.
    /// </remarks>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public AccountKind Kind { get; private init; }

    /// <summary>When it stopped being available for new money.</summary>
    public DateTimeOffset? RetiredAt { get; private set; }

    public bool IsOpen => RetiredAt is null;

    public void Rename(string name) => Name = Require(name, nameof(name));

    /// <summary>
    /// Stop new money being coded to it.
    /// </summary>
    /// <remarks>
    /// Silent when it is already retired rather than refusing, because somebody pressing the
    /// button twice has asked for a state this is already in — and a refusal there is an
    /// error message about nothing.
    /// </remarks>
    public void Retire(DateTimeOffset at)
    {
        if (!IsOpen)
        {
            return;
        }

        RetiredAt = at;
    }

    public void Reopen()
    {
        RetiredAt = null;
    }

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Coded(string value)
    {
        var code = Require(value, nameof(value)).ToUpperInvariant();

        return code.Length <= MaximumCodeLength ? code : code[..MaximumCodeLength];
    }

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

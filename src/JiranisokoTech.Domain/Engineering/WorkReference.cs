using System.Text.RegularExpressions;

namespace JiranisokoTech.Domain.Engineering;

/// <summary>
/// Finding the work item number in something a developer typed.
/// </summary>
/// <remarks>
/// This is the hinge the whole integration turns on. Nothing else connects a
/// commit to a task: the provider knows about branches and messages, this
/// system knows about work, and the only thing passing between them is whatever
/// a person happened to write while doing something else.
///
/// So the rule has to match what people actually do rather than what would be
/// convenient to parse. All of these are found:
///
///     feature/412-payment-api      the common convention
///     412-payment-api              the same without a prefix
///     bugfix/#412                  somebody being explicit
///     Fixes #412                   a commit message
///     #412 add the retry           a pull request title
///
/// And these are deliberately not, because each would attach work to the wrong
/// task and be believed:
///
///     v1.412.0                     a version number
///     release/2026-412             a date or a build
///     fix-412-errors               ambiguous: 412 could be an error code
///     PR-412                       the provider's number, not ours
///
/// The bias is towards finding nothing. A commit with no task attached is
/// visible and somebody can attach it; a commit attached to the wrong task is
/// invisible and stays wrong, and it puts evidence of work on a piece of work
/// that nobody did.
/// </remarks>
public static partial class WorkReference
{
    /// <summary>
    /// A number after a hash, or at the start of a branch segment.
    /// </summary>
    /// <remarks>
    /// Two alternatives, and the boundaries on each are the whole of the care
    /// here. The hash form needs no more than a hash. The branch form requires
    /// the number to open a segment — the start of the string or straight after
    /// a slash — which keeps `v1.412.0` and `fix-412-errors` out while letting
    /// `feature/412-payment-api` in.
    ///
    /// The lookahead then refuses a separator followed by more digits, and that
    /// clause was added because the test for `release/2026-412` failed: the
    /// segment rule alone matched the year and reported work item 2026. A
    /// number followed by a dash and more numbers is a date or a version
    /// somewhere in the world, and never a reference to one piece of work.
    /// </remarks>
    [GeneratedRegex(
        @"(?:#(?<hash>\d{1,9}))|(?:(?:^|/)(?<branch>\d{1,9})(?=$|[-_/](?!\d)))",
        RegexOptions.ExplicitCapture)]
    private static partial Regex Pattern { get; }

    /// <summary>
    /// The work item numbers named in a branch, message or title.
    /// </summary>
    /// <remarks>
    /// Several, because one commit legitimately closes two tasks and a pull
    /// request title often names both. Returned in the order they appear so
    /// that the first is the obvious primary one.
    /// </remarks>
    public static IReadOnlyList<int> In(params string?[] text) =>
        [.. text
            .Where(one => !string.IsNullOrWhiteSpace(one))
            .SelectMany(one => Pattern.Matches(one!).Cast<Match>())
            .Select(match => match.Groups["hash"].Success
                ? match.Groups["hash"].Value
                : match.Groups["branch"].Value)
            .Select(digits => int.TryParse(digits, out var number) ? number : 0)
            .Where(number => number > 0)
            .Distinct()];

    /// <summary>
    /// The one number to attach something to, or nothing.
    /// </summary>
    /// <remarks>
    /// The first found. When a commit names two tasks, attaching it to the
    /// first is a guess — but it is a visible guess on a screen somebody can
    /// correct, where attaching it to both would quietly double the evidence of
    /// work and attaching it to neither would lose it.
    /// </remarks>
    public static int? FirstIn(params string?[] text) => In(text) is [var first, ..]
        ? first
        : null;
}

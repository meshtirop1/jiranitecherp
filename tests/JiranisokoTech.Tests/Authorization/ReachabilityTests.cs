using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Authorization;

/// <summary>
/// Everything the application services can do, something can ask for.
/// </summary>
/// <remarks>
/// EnforcementTests catches a permission that nothing checks. This catches the
/// same fault one level down: a service method that nothing calls.
///
/// Both are the shape this codebase keeps producing. A capability gets built
/// carefully — domain rules, a service, persistence, tests — and then no screen
/// or endpoint ever reaches it. Everything passes. The work is real, the tests
/// are real, and nobody can use any of it.
///
/// What a sweep for this actually found: no screen could draft, publish or close
/// a job advert, so an approved requisition could never become a job anybody
/// applied to — while the careers site, the applications screen and the
/// interview screens all sat there waiting for applications that could not
/// arrive. Alongside it, a project could not be put on hold or cancelled or
/// given a lead, a logged hour could not be corrected before approval, a
/// department could not be renamed, a stalled approval could not be reassigned
/// or skipped, and an attachment could not be removed. Thirteen capabilities,
/// all finished, none reachable.
///
/// Reading source is a blunt instrument and it is the right one here: the
/// assertion is that a name appears somewhere it can do some good, and no
/// amount of running the application proves the absence of a call that was
/// never written.
/// </remarks>
public class ReachabilityTests
{
    /// <summary>
    /// Methods deliberately called by something other than a page or endpoint.
    /// </summary>
    /// <remarks>
    /// Each entry names what calls it. A method reaches this list by being
    /// genuinely internal — driven by an event handler rather than by a person
    /// — and not by being inconvenient to wire up.
    /// </remarks>
    private static readonly Dictionary<string, string> CalledByTheSystem = new()
    {
        ["ReleaseWorkOfAsync"] =
            "ReleaseWorkWhenSomebodyLeaves, from the EmployeeLeft event.",
        ["RequestUpTheLineAsync"] =
            "OpenTheChainWhenARequisitionIsSubmitted, from the RequisitionSubmitted event.",
        ["RequestAsync"] =
            "The general entry point to the approval engine, used by the handlers above "
            + "and by whatever opens a chain next. Not something a person does directly.",
        ["NoteUseAsync"] =
            "ApiKeyAuthenticationHandler, on every authenticated API request.",
        ["ResolveAsync"] =
            "ApiKeyAuthenticationHandler, to turn a bearer token into a key.",
    };

    [Fact]
    public void Every_service_method_can_be_reached()
    {
        var root = SolutionRoot();
        var web = Read(Path.Combine(root, "src", "JiranisokoTech.Web"));

        var unreachable = new List<string>();

        foreach (var (service, method) in ServiceMethods(root))
        {
            if (CalledByTheSystem.ContainsKey(method))
            {
                continue;
            }

            var called = web.Any(file =>
                file.Text.Contains($".{method}(", StringComparison.Ordinal));

            if (!called)
            {
                unreachable.Add($"{service}.{method}");
            }
        }

        Assert.True(
            unreachable.Count == 0,
            "These application service methods are built and tested, and nothing in the "
            + "application can call them — so the capability exists and nobody can use it:\n  "
            + string.Join("\n  ", unreachable)
            + "\n\nEither give it a screen or an endpoint, or add it to CalledByTheSystem "
            + "with a note saying what does call it.");
    }

    /// <summary>
    /// Every entry in the exemption list is a method that still exists.
    /// </summary>
    /// <remarks>
    /// An exemption outliving its method is how a list like this turns into a
    /// place where things go to be forgotten.
    /// </remarks>
    [Fact]
    public void The_exemption_list_has_nothing_stale_in_it()
    {
        var methods = ServiceMethods(SolutionRoot())
            .Select(found => found.Method)
            .ToHashSet(StringComparer.Ordinal);

        var stale = CalledByTheSystem.Keys.Where(name => !methods.Contains(name)).ToList();

        Assert.True(
            stale.Count == 0,
            "These are exempted from the reachability check and no longer exist: "
            + string.Join(", ", stale));
    }

    private static IEnumerable<(string Service, string Method)> ServiceMethods(string root)
    {
        var application = Path.Combine(root, "src", "JiranisokoTech.Application");

        foreach (var file in Read(application).Where(one => one.Path.Contains("Service")))
        {
            var service = Path.GetFileNameWithoutExtension(file.Path);

            foreach (Match match in Regex.Matches(
                file.Text, @"public\s+(?:async\s+)?Task(?:<[^>]*>)?\s+(\w+Async)\s*\("))
            {
                yield return (service, match.Groups[1].Value);
            }
        }
    }

    private static List<(string Path, string Text)> Read(string folder) =>
        [.. Directory
            .EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".razor", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (path, File.ReadAllText(path)))];

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "JiranisokoTech.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}

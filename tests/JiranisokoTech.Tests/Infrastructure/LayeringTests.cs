using System.Reflection;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The layers depend inwards and only inwards.
/// </summary>
/// <remarks>
/// Section 79 asks for strong domain boundaries, and docs/architecture.md describes them: the
/// domain knows nothing, the application layer knows the domain, infrastructure knows both and
/// no web framework, and only the web project knows HTTP. The document said so and nothing
/// checked it, so the first convenient reference from the domain to Entity Framework, or from
/// a service to <c>HttpContext</c>, would have made the document false without failing anything.
///
/// Read from the compiled assemblies rather than the project files, because what matters is
/// what the code actually uses — a package reference that is never touched does no harm, and
/// a type reached through a transitive reference does.
/// </remarks>
public class LayeringTests
{
    private static readonly Assembly Domain = typeof(JiranisokoTech.Domain.Work.WorkItem).Assembly;
    private static readonly Assembly Application = typeof(JiranisokoTech.Application.Work.WorkService).Assembly;
    private static readonly Assembly Infrastructure = typeof(JiranisokoTech.Infrastructure.Persistence.AppDbContext).Assembly;

    [Fact]
    public void The_domain_depends_on_nothing_of_ours_and_on_no_framework()
    {
        var references = Names(Domain);

        Assert.DoesNotContain(references, name => name.StartsWith("JiranisokoTech.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void The_application_layer_knows_the_domain_and_not_how_anything_is_stored_or_served()
    {
        var references = Names(Application);

        Assert.Contains("JiranisokoTech.Domain", references);
        Assert.DoesNotContain("JiranisokoTech.Infrastructure", references);
        Assert.DoesNotContain("JiranisokoTech.Web", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    /// <summary>
    /// Infrastructure stores and sends, and does not know about requests.
    /// </summary>
    /// <remarks>
    /// Identity's EF stores and Data Protection are allowed: they are persistence and
    /// cryptography that happen to live under the AspNetCore name. What is refused is the part
    /// of ASP.NET that is about a request — HTTP abstractions, routing, components — because a
    /// service that reaches for the current request works in a page and throws in a job.
    /// </remarks>
    [Fact]
    public void Infrastructure_does_not_know_about_requests()
    {
        var references = Names(Infrastructure);

        // Proves the reading works at all: an empty set would pass every refusal below.
        Assert.Contains("JiranisokoTech.Application", references);
        Assert.DoesNotContain("JiranisokoTech.Web", references);

        string[] requestShaped =
        [
            "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Http.Abstractions",
            "Microsoft.AspNetCore.Routing", "Microsoft.AspNetCore.Components",
            "Microsoft.AspNetCore.Mvc.Core",
        ];

        Assert.Empty(references.Intersect(requestShaped));
    }

    private static HashSet<string> Names(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(name => name.Name!).ToHashSet();
}

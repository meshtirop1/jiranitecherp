using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// Every link between the documents goes somewhere.
/// </summary>
/// <remarks>
/// The README had become a record of the project as it stood weeks earlier — "State:
/// foundation", 522 tests, Redis provisioned and unused — and its Docker instructions put the
/// <c>.env</c> file where Compose does not look, so following them ended at "set
/// POSTGRES_PASSWORD" with the password already set. It now points at the documents that are
/// kept current instead of restating them. What this checks is the part a test can: that a
/// link from one document to another has something at the end of it.
/// </remarks>
public partial class DocumentationTests
{
    [Fact]
    public void Every_relative_link_in_the_documents_resolves()
    {
        var root = Root();
        var documents = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md")
            .Append(Path.Combine(root, "README.md"));

        var broken = new List<string>();

        foreach (var document in documents)
        {
            var folder = Path.GetDirectoryName(document)!;

            foreach (Match link in Links().Matches(File.ReadAllText(document)))
            {
                var target = link.Groups["target"].Value.Split('#')[0];

                if (target.Length == 0 || target.Contains("://", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!File.Exists(Path.GetFullPath(Path.Combine(folder, target))))
                {
                    broken.Add($"{Path.GetFileName(document)} → {target}");
                }
            }
        }

        Assert.True(broken.Count == 0, "Links to nothing:\n  " + string.Join("\n  ", broken));
    }

    [Fact]
    public void The_architecture_document_covers_every_heading_section_2_asks_for()
    {
        var architecture = File.ReadAllText(Path.Combine(Root(), "docs", "architecture.md"));

        foreach (var heading in new[]
                 {
                     "Current architecture", "Proposed architecture", "Database strategy",
                     "Module structure", "Authentication strategy", "Authorization strategy",
                     "Integration strategy", "Event architecture", "Background jobs",
                     "AI architecture", "Deployment architecture", "Testing strategy",
                 })
        {
            Assert.Contains($"## {heading}", architecture);
        }
    }

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)\)")]
    private static partial Regex Links();

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".env.example")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }
}

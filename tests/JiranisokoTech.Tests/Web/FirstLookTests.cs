using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A statically rendered form seeded behind a first-pass flag also asks FirstLook.
/// </summary>
/// <remarks>
/// <b>This exists because a rule in CLAUDE.md did not stop the fault coming back.</b> A page
/// fills its form from what is stored, behind a <c>_loaded</c> or <c>_filled</c> field set on
/// the first pass. Under static rendering every request is a new instance, so on a POST the
/// field is false again, the stored values go back over the posted ones, and the handler saves
/// what was already there while the page reports success. It was fixed on four pages and
/// written down; an audit then found it on three more — the staff record's pay and contact
/// forms, your own profile, and the candidate page — which between them meant nobody's salary
/// could be recorded from the screen, so every pay run came out empty.
///
/// So the shape is checked rather than remembered. Components that run with a circuit are
/// left out, because there the flag is the right guard and there is no request to ask.
/// </remarks>
public partial class FirstLookTests
{
    [Fact]
    public void Every_first_pass_guard_on_a_static_page_asks_FirstLook()
    {
        var web = WebProject();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);

            if (lines.Any(line => line.TrimStart().StartsWith("@rendermode", StringComparison.Ordinal)))
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (FirstPassGuard().IsMatch(lines[i]) && !lines[i].Contains("FirstLook.ThisTime"))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A first-pass flag does nothing under static rendering: on a POST it is false again, "
            + "and the seeding puts the stored values back over the posted ones before the handler "
            + "reads them. Add FirstLook.ThisTime(Request) to the condition:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A form's model written in a lifecycle method that never asks FirstLook.
    /// </summary>
    /// <remarks>
    /// The shape the flag check above cannot see, because there is no flag. The payroll page
    /// set its dates to last month in <c>OnInitializedAsync</c> with no guard of any kind, so
    /// on the POST the period somebody typed was replaced before the handler read it: only the
    /// month just gone could ever be drafted, and asking for another drafted that one and said
    /// "Drafted". A plain assignment is what is looked for; <c>??=</c> is left alone, because
    /// it leaves a posted value where it is, and so is a write the method guards by checking the
    /// same member is still empty.
    /// </remarks>
    [Fact]
    public void No_static_page_writes_a_forms_model_on_every_request()
    {
        var web = WebProject();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            if (text.Contains("@rendermode", StringComparison.Ordinal))
            {
                continue;
            }

            var bound = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match binding in BoundModels().Matches(text))
            {
                bound.Add(binding.Groups["property"].Value);

                if (binding.Groups["field"].Success)
                {
                    bound.Add(binding.Groups["field"].Value);
                }
            }

            foreach (Match method in Lifecycle().Matches(text))
            {
                var body = Body(text, method.Index + method.Length);

                if (body.Contains("FirstLook.ThisTime", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match write in Writes().Matches(body))
                {
                    var target = write.Groups["target"].Value;

                    /*
                     * Filling a value only while it is still empty is safe — a posted value is
                     * not empty — and it is how the date boxes default to today. So a write to a
                     * model the method checks against default or null is let through; the leave
                     * form fills both dates when the first is empty, which is the same guard.
                     */
                    var empty = new Regex(
                        $@"\b{Regex.Escape(target)}(\.\w+)*\s*(==\s*(default|null)|is\s+null)");

                    if (bound.Contains(target) && !empty.IsMatch(body))
                    {
                        offenders.Add(
                            $"{Path.GetFileName(file)}: {method.Groups["name"].Value} writes "
                            + write.Value.Trim());
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A lifecycle method runs on the POST as well, before the handler, so writing a form's "
            + "model there replaces what was just posted. Guard it with FirstLook.ThisTime(Request):"
            + "\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The body of the method whose header ends at <paramref name="from"/>.</summary>
    private static string Body(string text, int from)
    {
        var open = text.IndexOf('{', from);
        var arrow = text.IndexOf("=>", from, StringComparison.Ordinal);

        if (arrow >= 0 && (open < 0 || arrow < open))
        {
            var end = text.IndexOf(';', arrow);

            return text[arrow..(end < 0 ? text.Length : end)];
        }

        var depth = 0;

        for (var i = open; i < text.Length; i++)
        {
            depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };

            if (depth == 0)
            {
                return text[open..(i + 1)];
            }
        }

        return text[open..];
    }

    [GeneratedRegex(@"\bif\s*\(.*\b_(loaded|filled|seeded)\b")]
    private static partial Regex FirstPassGuard();

    /// <summary>A form-bound property, and the field behind it when it has one.</summary>
    [GeneratedRegex(
        @"\[SupplyParameterFromForm[^\]]*\]\s*(?:private|public|protected|internal)\s+[\w<>?]+\s+(?<property>\w+)\s*\{(?:\s*get\s*=>\s*(?<field>_\w+)\s*;)?")]
    private static partial Regex BoundModels();

    [GeneratedRegex(
        @"protected\s+override\s+(?:async\s+)?(?:Task|void)\s+(?<name>OnInitialized(?:Async)?|OnParametersSet(?:Async)?)\s*\(\s*\)")]
    private static partial Regex Lifecycle();

    /// <summary><c>Input = …</c>, <c>_input.From = …</c>; not <c>==</c>, not <c>??=</c>.</summary>
    [GeneratedRegex(@"(?<![\w.])(?<target>_?\w+)(?:\.\w+)*\s*(?<![?!=<>])=(?!=)")]
    private static partial Regex Writes();

    private static string WebProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
            && !Directory.Exists(Path.Combine(directory.FullName, "src", "JiranisokoTech.Web")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!.FullName, "src", "JiranisokoTech.Web");
    }
}

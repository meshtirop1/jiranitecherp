using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A form rendered once per row must not carry a field.
/// </summary>
/// <remarks>
/// <b>This exists because the same fault kept coming back, and a rule in CLAUDE.md did not stop
/// it.</b> Blazor refuses a page holding two forms of the same name, so a form rendered per row
/// is named after its row — <c>move-{id}</c>. A model is bound by
/// <c>[SupplyParameterFromForm(FormName = "move")]</c>, which matches one exact name and has
/// nowhere to put an id. So nothing typed into such a form ever reaches its handler.
///
/// It had been fixed four times when an audit found ten more, on seven pages: revoking an API
/// key was refused for want of the reason just typed, so a leaked key could not be stopped;
/// moving an opportunity always moved it to the default stage; writing down what happened on
/// one was refused as empty; reassigning or skipping an approval step never saw the person or
/// reason chosen; and tagging a document, recording an exit interview, moving a repository to
/// a project, claiming a contributor handle and correcting a time entry all dropped their
/// input the same way. Every one of them passed <c>EnforcementTests</c>, because the permission
/// was checked, and <c>ReachabilityTests</c>, because the service had a caller. What was wrong
/// is visible only in the markup, so this reads the markup.
///
/// Two shapes are fine, and this lets both through. A per-row form with no field needs nothing
/// posted, because its handler closes over the row's id. And a model bound by a bare
/// <c>[SupplyParameterFromForm]</c>, with no name, is bound from whichever form was posted — so
/// it takes the row's values whatever the form is called. That was proved by posting the API
/// key revocation form both ways: named, the reason arrived empty and the key stayed live;
/// unnamed, the key was revoked with the reason typed. Forms that read
/// <c>Request.Form</c> directly bind no model and are also left alone.
/// </remarks>
public partial class PerRowFormTests
{
    [Fact]
    public void No_form_named_after_its_row_carries_a_field()
    {
        var web = WebProject();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            var boundByName = NamedBindings().Matches(text)
                .Select(binding => binding.Groups["property"].Value)
                .ToHashSet();

            foreach (Match form in Forms().Matches(text))
            {
                var name = form.Groups["name"].Value;
                var body = form.Groups["body"].Value;
                var model = form.Groups["model"].Value;

                if (name.Contains('{') && Fields().IsMatch(body) && boundByName.Contains(model))
                {
                    var line = text[..form.Index].Count(character => character == '\n') + 1;

                    offenders.Add($"{Path.GetFileName(file)}:{line}  {name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A form named after its row cannot bind a model, because [SupplyParameterFromForm] "
            + "matches one exact name and has nowhere to put the row's id. Whatever is typed "
            + "into it never reaches the handler: a required field is refused as empty and a "
            + "field with a default silently submits the default. Bind the model with a bare "
            + "[SupplyParameterFromForm], which binds from whichever form was posted:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// An <c>EditForm</c> whose name is an interpolated string: its model, its name, and what it
    /// holds. Blazor writes the attributes in any order, so the model is found by a lookahead.
    /// </summary>
    [GeneratedRegex(
        """<EditForm\b(?=[^>]*?Model="(?<model>\w+)")[^>]*?FormName="@\(\$"(?<name>[^"]*)"\)"[^>]*>(?<body>.*?)</EditForm>""",
        RegexOptions.Singleline)]
    private static partial Regex Forms();

    /// <summary>A model bound to one form by name, and the property it is bound to.</summary>
    [GeneratedRegex(
        """\[SupplyParameterFromForm\(FormName\s*=\s*"[^"]*"\)\]\s*(?:private|public|protected|internal)\s+\w+\??\s+(?<property>\w+)""")]
    private static partial Regex NamedBindings();

    /// <summary>
    /// Anything that posts a value somebody chose: Blazor's input components, or a plain
    /// input, select or textarea. A hidden input is not a choice, and a submit button posts
    /// nothing that is bound.
    /// </summary>
    [GeneratedRegex(
        """<Input[A-Z]\w*|<select\b|<textarea\b|<input\b(?![^>]*type="(hidden|submit)")""")]
    private static partial Regex Fields();

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

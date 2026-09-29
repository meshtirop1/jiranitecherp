using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Application;

/// <summary>
/// An optional code or slug falls back to the name when it is blank, not only when it is null.
/// </summary>
/// <remarks>
/// <b>Taking on a client through the page failed whenever its optional code was left empty.</b>
/// A browser posts an empty text box as "", the service wrote <c>Slug.From(code ?? name)</c>,
/// "" is not null, and a slug of nothing is refused — with a message about a parameter called
/// <c>text</c> that meant nothing to the person looking at the form. The same line was in four
/// more services. Found by the finance workflow test, which posts the client form as a browser
/// does; every earlier test called the service with the code left out, which is null, which
/// works.
///
/// Checked by shape, because the next service that offers an optional code will be written
/// by copying one of these.
/// </remarks>
public partial class SlugFallbackTests
{
    [Fact]
    public void No_service_falls_back_to_the_name_only_when_the_code_is_null()
    {
        var application = Path.Combine(Root(), "src", "JiranisokoTech.Application");

        var offenders = Directory.EnumerateFiles(application, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, index) => (file, line, index)))
            .Where(one => NullOnlyFallback().IsMatch(one.line))
            .Select(one => $"{Path.GetFileName(one.file)}:{one.index + 1}  {one.line.Trim()}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A form posts an empty box as \"\", which ?? does not replace. Use "
            + "string.IsNullOrWhiteSpace(x) ? name : x:\n  " + string.Join("\n  ", offenders));
    }

    [GeneratedRegex(@"Slug\.From\(\s*\w+\s*\?\?")]
    private static partial Regex NullOnlyFallback();

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JiranisokoTech.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}

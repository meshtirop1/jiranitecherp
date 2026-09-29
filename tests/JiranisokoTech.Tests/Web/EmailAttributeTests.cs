using JiranisokoTech.Web;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Email fields are checked by an attribute that lets an empty box through.
/// </summary>
/// <remarks>
/// <c>[EmailAddress]</c> refuses an empty string, and a browser posts an empty box as one, so
/// it made every optional email field in the application required — and silently, because the
/// forms carry no message beside those boxes. Somebody without a personal email on file could
/// not save their phone number. See <see cref="EmailOrBlankAttribute"/>.
/// </remarks>
public class EmailAttributeTests
{
    [Fact]
    public void No_form_in_the_web_project_uses_EmailAddress()
    {
        var web = WebProject();

        var offenders = Directory
            .EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains("[EmailAddress"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "[EmailAddress] refuses an empty box, which makes an optional email field required "
            + "without saying so. Use [EmailOrBlank], with [Required] beside it where the field "
            + "really is required:\n  " + string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("grace@example.com", true)]
    [InlineData(" grace@example.com ", true)]
    [InlineData("grace", false)]
    [InlineData("@example.com", false)]
    public void Blank_is_accepted_and_anything_typed_must_look_like_an_address(
        string? value, bool valid) =>
        Assert.Equal(valid, new EmailOrBlankAttribute().IsValid(value));

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

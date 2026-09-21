using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// A form as a browser would fill and send it.
/// </summary>
/// <remarks>
/// Every hidden field is carried back — the antiforgery token and the handler
/// name among them — because that is precisely what a browser does and what the
/// server checks. Naming those fields in the test instead would make it pass
/// against a page that had stopped emitting them.
/// </remarks>
public static partial class HtmlForm
{
    public static Dictionary<string, string> Fill(
        string html,
        IReadOnlyDictionary<string, string>? values = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match input in InputTag().Matches(html))
        {
            var tag = input.Value;
            var name = Attribute(tag, "name");

            if (name is null)
            {
                continue;
            }

            var type = Attribute(tag, "type");

            // A browser sends neither the submit button it did not press nor an
            // unticked checkbox. Copying them in would test a request no
            // browser makes.
            if (type is "submit" or "button")
            {
                continue;
            }

            if (type == "checkbox" && !tag.Contains("checked", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            fields[name] = Attribute(tag, "value") ?? string.Empty;
        }

        if (values is not null)
        {
            foreach (var (name, value) in values)
            {
                fields[name] = value;
            }
        }

        return fields;
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(
            tag, $@"\b{name}\s*=\s*""(?<value>[^""]*)""", RegexOptions.IgnoreCase);

        return match.Success
            ? System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value)
            : null;
    }

    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputTag();
}

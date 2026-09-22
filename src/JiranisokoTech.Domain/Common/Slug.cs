using System.Globalization;
using System.Text;

namespace JiranisokoTech.Domain.Common;

/// <summary>
/// A short, stable name for something, safe to put in a URL.
/// </summary>
/// <remarks>
/// A value object rather than a string, because a slug has rules and a string
/// has none. Every place that accepted a raw string would have to remember to
/// lower-case it, strip the punctuation and collapse the spaces, and the one
/// that forgot would produce "Field Operations" in an address bar and a
/// duplicate row that only differs by case.
///
/// It is derived from a name once and then kept. Re-deriving it whenever the
/// name changes would silently break every link anybody had saved.
/// </remarks>
public readonly record struct Slug
{
    public const int MaximumLength = 120;

    private Slug(string value) => Value = value;

    public string Value { get; }

    public static Slug From(string text)
    {
        var slug = Reduce(text);

        if (slug.Length == 0)
        {
            throw new ArgumentException(
                $"'{text}' has nothing in it that can be used as a slug.", nameof(text));
        }

        return new Slug(slug);
    }

    /// <summary>For reading a value back out of the database, which was valid when written.</summary>
    public static Slug FromStored(string value) => new(value);

    public static bool TryFrom(string text, out Slug slug)
    {
        var reduced = Reduce(text);

        slug = new Slug(reduced);

        return reduced.Length > 0;
    }

    public override string ToString() => Value;

    public static implicit operator string(Slug slug) => slug.Value;

    private static string Reduce(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Decomposed first, so an accented letter becomes its base letter plus a
        // mark and the mark can be dropped. Without this, "Operações" would lose
        // the whole letter rather than just the accent.
        var normalised = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalised.Length);
        var pendingSeparator = false;

        foreach (var character in normalised)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingSeparator = false;
                builder.Append(char.ToLowerInvariant(character));

                continue;
            }

            // Runs of anything else collapse to a single dash, and only once
            // something follows them — so no slug begins or ends with one.
            pendingSeparator = true;
        }

        var slug = builder.ToString();

        return slug.Length > MaximumLength ? slug[..MaximumLength].TrimEnd('-') : slug;
    }
}

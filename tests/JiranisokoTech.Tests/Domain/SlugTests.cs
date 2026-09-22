using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Tests.Domain;

/// <summary>
/// The short name that goes in an address.
/// </summary>
/// <remarks>
/// A value object rather than a string because every one of these rules is one
/// that a caller passing a raw string would eventually forget, and the one that
/// forgot would put a space in a URL or create a duplicate that differs only by
/// case.
/// </remarks>
public class SlugTests
{
    [Theory]
    [InlineData("Field Operations", "field-operations")]
    [InlineData("Engineering", "engineering")]
    [InlineData("R&D", "r-d")]
    [InlineData("People  &   Culture", "people-culture")]
    [InlineData("  Delivery  ", "delivery")]
    [InlineData("Phase 2", "phase-2")]
    public void A_name_becomes_a_handle(string name, string expected)
    {
        Assert.Equal(expected, Slug.From(name).Value);
    }

    /// <summary>
    /// Decomposed first, so an accent is dropped and the letter under it is
    /// kept. Stripping the whole character would turn "Operações" into a word
    /// missing letters rather than missing accents.
    /// </summary>
    [Theory]
    [InlineData("Operações", "operacoes")]
    [InlineData("Café Team", "cafe-team")]
    public void Accents_are_removed_and_their_letters_kept(string name, string expected)
    {
        Assert.Equal(expected, Slug.From(name).Value);
    }

    [Theory]
    [InlineData("--Delivery--", "delivery")]
    [InlineData("!!!", null)]
    [InlineData("   ", null)]
    public void Punctuation_never_starts_or_ends_a_handle(string name, string? expected)
    {
        if (expected is null)
        {
            Assert.False(Slug.TryFrom(name, out _));

            return;
        }

        Assert.Equal(expected, Slug.From(name).Value);
    }

    [Fact]
    public void A_name_with_nothing_usable_in_it_is_refused()
    {
        var refused = Assert.Throws<ArgumentException>(() => Slug.From("###"));

        Assert.Contains("###", refused.Message);
    }

    [Fact]
    public void A_very_long_name_is_cut_to_fit_the_column()
    {
        var slug = Slug.From(new string('a', 400));

        Assert.Equal(Slug.MaximumLength, slug.Value.Length);
    }

    /// <summary>
    /// Trimming must not leave a trailing dash, which would be a handle that
    /// looks like a typo in every address it appears in.
    /// </summary>
    [Fact]
    public void Cutting_a_long_name_does_not_leave_a_dangling_dash()
    {
        var slug = Slug.From(string.Join(' ', Enumerable.Repeat("word", 60)));

        Assert.DoesNotContain("--", slug.Value);
        Assert.False(slug.Value.EndsWith('-'));
    }

    [Fact]
    public void Two_handles_from_the_same_name_are_the_same_value()
    {
        Assert.Equal(Slug.From("Field Operations"), Slug.From("field operations"));
    }

    [Fact]
    public void A_handle_reads_as_its_text()
    {
        Slug slug = Slug.From("Delivery");

        Assert.Equal("delivery", slug.ToString());
        Assert.Equal("delivery", (string)slug);
    }
}

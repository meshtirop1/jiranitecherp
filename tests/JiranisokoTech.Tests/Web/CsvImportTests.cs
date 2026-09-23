using JiranisokoTech.Web.Reporting;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Reading a spreadsheet somebody made.
/// </summary>
/// <remarks>
/// Reading is the direction where a mistake costs something. A wrongly-written export looks
/// wrong on screen; a wrongly-read import writes four hundred rows and the person who ran it
/// has no idea which ones are wrong.
///
/// Most of these are about what a real spreadsheet produces rather than about the format —
/// byte order marks, trailing blank lines, commas inside names — because an importer that
/// only reads files a specification would approve of is one nobody can use with the file
/// they have.
/// </remarks>
public class CsvImportTests
{
    /// <summary>
    /// Excel's byte order mark does not become part of the first column's name.
    /// </summary>
    /// <remarks>
    /// The single most likely thing to break a first attempt. Excel writes one when it saves
    /// as UTF-8, and left in place it makes the first column never match — so every row is
    /// reported as missing the one field that is required, on a file that looks correct in
    /// every editor.
    /// </remarks>
    [Fact]
    public void A_byte_order_mark_is_not_part_of_the_first_column()
    {
        var file = CsvReader.Read("﻿name,code\nAcme,acme\n");

        Assert.True(file.IsReadable);
        Assert.Equal(["name", "code"], file.Columns);
        Assert.Equal("Acme", Assert.Single(file.Rows)["name"]);
    }

    /// <summary>
    /// A comma inside a quoted field is part of the name.
    /// </summary>
    /// <remarks>
    /// The ordinary case rather than an edge one: "Smith, Jones and Partners" is what a firm
    /// of that name is called, and splitting on commas would import two clients, one of them
    /// called "Jones and Partners".
    /// </remarks>
    [Fact]
    public void A_comma_inside_a_name_does_not_split_it()
    {
        var file = CsvReader.Read("name,code\n\"Smith, Jones and Partners\",sjp\n");

        var row = Assert.Single(file.Rows);

        Assert.Equal("Smith, Jones and Partners", row["name"]);
        Assert.Equal("sjp", row["code"]);
    }

    /// <summary>Two quotes inside a quoted field are one quote.</summary>
    [Fact]
    public void An_escaped_quote_is_read_as_one()
    {
        var file = CsvReader.Read("name\n\"The \"\"Big\"\" Company\"\n");

        Assert.Equal("The \"Big\" Company", Assert.Single(file.Rows)["name"]);
    }

    /// <summary>
    /// A trailing blank line is ignored rather than refused.
    /// </summary>
    /// <remarks>
    /// Every spreadsheet writes one. Refusing it would mean refusing every real file, and
    /// importing it would create a client with no name.
    /// </remarks>
    [Fact]
    public void A_trailing_blank_line_is_ignored()
    {
        var file = CsvReader.Read("name,code\r\nAcme,acme\r\n\r\n");

        Assert.Single(file.Rows);
    }

    /// <summary>
    /// Columns are matched by name, not by position.
    /// </summary>
    /// <remarks>
    /// Positional columns mean a spreadsheet whose columns somebody tidied imports names into
    /// the code field, silently and completely. The header is also matched without regard to
    /// case or surrounding space, because "Client Name" and "client name " are the same column
    /// to everybody except a string comparison.
    /// </remarks>
    [Fact]
    public void Columns_are_matched_by_name_however_they_are_ordered()
    {
        var file = CsvReader.Read("Code , NAME\nacme,Acme Haulage\n");

        var row = Assert.Single(file.Rows);

        Assert.Equal("Acme Haulage", row["name"]);
        Assert.Equal("acme", row["code"]);
    }

    /// <summary>
    /// Two columns with the same name are refused rather than resolved.
    /// </summary>
    /// <remarks>
    /// One of them would silently win, and which one depends on the order they happen to be
    /// in — so an import that looked right once would import different data the next time
    /// somebody moved a column.
    /// </remarks>
    [Fact]
    public void Two_columns_with_one_name_are_refused()
    {
        var file = CsvReader.Read("name,email,email\nAcme,a@b.co,c@d.co\n");

        Assert.False(file.IsReadable);
        Assert.Contains("two columns", file.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        Assert.False(CsvReader.Read(string.Empty).IsReadable);
        Assert.False(CsvReader.Read("\n").IsReadable);
    }

    /// <summary>
    /// A missing cell is nothing rather than an exception.
    /// </summary>
    /// <remarks>
    /// A row shorter than the header is what a spreadsheet writes when trailing cells are
    /// blank, and it is not an error — it is a client with no email.
    /// </remarks>
    [Fact]
    public void A_short_row_reads_its_missing_cells_as_nothing()
    {
        var file = CsvReader.Read("name,code,email\nAcme,acme\n");

        var row = Assert.Single(file.Rows);

        Assert.Equal("Acme", row["name"]);
        Assert.Null(row["email"]);
    }

    /// <summary>
    /// The line number is the file's own, not the row's index.
    /// </summary>
    /// <remarks>
    /// So a message about line 43 sends somebody to line 43 of their spreadsheet. Reporting
    /// the forty-third data row would send them two lines out, which on a four-hundred-row
    /// file is an afternoon.
    /// </remarks>
    [Fact]
    public void A_row_knows_which_line_of_the_file_it_is()
    {
        var file = CsvReader.Read("name\nFirst\nSecond\nThird\n");

        Assert.Equal([2, 3, 4], file.Rows.Select(row => row.Line));
    }

    /// <summary>A required column that is absent is named.</summary>
    [Fact]
    public void A_missing_required_column_is_named()
    {
        var file = CsvReader.Read("code,email\nacme,a@b.co\n");

        Assert.Equal("name", file.Missing("name", "code"));
        Assert.Null(file.Missing("code"));
    }
}

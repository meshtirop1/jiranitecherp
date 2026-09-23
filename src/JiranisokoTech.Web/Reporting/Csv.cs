using System.Text;

namespace JiranisokoTech.Web.Reporting;

/// <summary>
/// Writing a comma-separated file that a spreadsheet cannot turn against its
/// reader.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from a library, because the whole of the
/// format is three rules and the interesting work is not the format at all. A
/// CSV is not read by a program that was told what to expect; it is opened by
/// somebody in Excel, and Excel is a language interpreter. Two of the three
/// rules here exist because of that and not because of RFC 4180.
/// </remarks>
public static class Csv
{
    /// <summary>
    /// The characters a spreadsheet reads as the start of a formula.
    /// </summary>
    /// <remarks>
    /// A cell beginning with any of these is executed on open, by Excel, by
    /// LibreOffice and by Google Sheets. The consequences are not theoretical:
    /// =HYPERLINK, =WEBSERVICE and DDE formulas turn an exported report into a
    /// way of moving whatever else is in the spreadsheet to somebody's server,
    /// and the person who opens the file is by definition somebody senior enough
    /// to be reading the firm's figures.
    ///
    /// This file carries text people typed — employees' names on the away list,
    /// and the client and project names the other exports will carry — so the
    /// content is not ours to trust. Nobody has to be an attacker for it to
    /// matter either: a name pasted from a system that prefixed it with a hyphen
    /// arrives as a formula error in the cell, and the report simply looks
    /// broken.
    /// </remarks>
    private static readonly char[] Formula = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>The characters that force a field to be wrapped in quotes.</summary>
    private static readonly char[] Quoted = [',', '"', '\r', '\n'];

    /// <summary>
    /// The rows as a file, with the line ending the format actually specifies.
    /// </summary>
    /// <remarks>
    /// CRLF rather than the platform's own, because the file is written on Linux
    /// in a container and opened on Windows by a person, and the one thing every
    /// spreadsheet on every platform accepts is the one the format names.
    /// </remarks>
    public static string Sheet(IEnumerable<IReadOnlyList<string?>> rows) =>
        string.Concat(rows.Select(row => Row(row) + "\r\n"));

    public static string Row(IReadOnlyList<string?> fields) =>
        string.Join(",", fields.Select(Field));

    /// <summary>One field, quoted if it has to be and defused if it has to be.</summary>
    public static string Field(string? value)
    {
        var text = value ?? string.Empty;

        if (text.Length > 0 && Formula.Contains(text[0]))
        {
            /*
             * A leading apostrophe, which every spreadsheet reads as "this cell
             * is text" and shows in the formula bar rather than the cell.
             *
             * Quoting is not a defence here and it is the mistake that gets
             * made: "=1+1" in quotes is still a formula, because the quotes are
             * the CSV's own escaping and are gone before the cell is parsed.
             * Stripping the character instead would silently alter somebody's
             * name, which is worse than a visible tick in front of it.
             *
             * Nothing this file writes legitimately begins with one of these:
             * money carries its currency code in front of the amount, so not
             * even a negative figure starts with a minus.
             */
            text = "'" + text;
        }

        // Quotes doubled inside quotes, which is the whole of the escaping the
        // format has. A field is only wrapped when it needs to be, so a file of
        // plain figures stays readable in a text editor.
        return text.IndexOfAny(Quoted) >= 0
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    /// <summary>
    /// The file as bytes, with a byte order mark in front of it.
    /// </summary>
    /// <remarks>
    /// The mark is there for Excel on Windows, which reads a CSV without one in
    /// the machine's ANSI code page regardless of what the response says it is.
    /// Every Kenyan name with an apostrophe or an accent in it then arrives
    /// mangled, and the reader concludes the payroll data is corrupt. Three
    /// bytes, and the alternative is explaining this to somebody once a month.
    /// </remarks>
    public static byte[] Bytes(string sheet) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sheet)];
}

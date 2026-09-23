namespace JiranisokoTech.Web.Reporting;

/// <summary>
/// Reading a comma-separated file somebody made in a spreadsheet.
/// </summary>
/// <remarks>
/// Section 52 had export and no import, and the asymmetry is the wrong way round: exporting
/// is a convenience, and importing is what somebody does on their first afternoon with a
/// new system, holding three years of client records in a spreadsheet.
///
/// Hand-written for the same reason <see cref="Csv"/> is: the format is a handful of rules
/// and the interesting work is elsewhere. What is different here is that reading is the
/// direction where a mistake costs something. A wrongly-written export looks wrong on
/// screen; a wrongly-read import writes four hundred rows into the database and the person
/// who ran it has no idea which ones are wrong.
///
/// So three decisions shape this file, and none of them is about the format:
///
/// <b>The whole file is read and checked before anything is written.</b> Importing row by
/// row means an import that fails on row three hundred has already committed two hundred
/// and ninety-nine, and nobody knows which. See the importer.
///
/// <b>A header row is required and matched by name.</b> Positional columns mean a
/// spreadsheet with its columns reordered — which is what happens when somebody tidies it —
/// imports names into the code field, silently and completely.
///
/// <b>Excel's own quirks are accommodated rather than corrected.</b> A byte order mark, a
/// trailing empty line, CRLF endings: all of those are what a spreadsheet produces, and an
/// importer that refused them would be an importer nobody could use with the file they
/// have.
/// </remarks>
public static class CsvReader
{
    /// <summary>
    /// Read a file into rows keyed by their column name.
    /// </summary>
    /// <remarks>
    /// Column names are lower-cased and trimmed, because a header typed "Client Name" and
    /// one typed "client name " are the same column to everybody except a string comparison.
    /// </remarks>
    public static CsvFile Read(string text)
    {
        // A byte order mark is what Excel writes when it saves as UTF-8, and it would
        // otherwise become part of the first column's name — so the first column would
        // never match and every row would be missing it.
        var cleaned = text.TrimStart('﻿');

        var lines = Split(cleaned);

        if (lines.Count == 0)
        {
            return new CsvFile([], [], "The file is empty.");
        }

        var header = lines[0]
            .Select(name => name.Trim().ToLowerInvariant())
            .ToList();

        if (header.All(string.IsNullOrEmpty))
        {
            return new CsvFile([], [], "The first line has no column names on it.");
        }

        var duplicated = header
            .Where(name => name.Length > 0)
            .GroupBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicated is not null)
        {
            /*
             * Refused rather than resolved. Two columns called "email" means one of them
             * would silently win, and which one depends on the order they happen to be in —
             * so an import that looked right once would import different data the next time
             * somebody moved a column.
             */
            return new CsvFile(
                [], [], $"There are two columns called '{duplicated.Key}'. Rename one.");
        }

        var rows = new List<CsvRow>();

        for (var index = 1; index < lines.Count; index++)
        {
            var values = lines[index];

            // A wholly blank line is skipped rather than refused. Every spreadsheet writes
            // one at the end, and refusing it would mean refusing every real file.
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var cells = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var column = 0; column < header.Count; column++)
            {
                if (header[column].Length == 0)
                {
                    continue;
                }

                cells[header[column]] = column < values.Count ? values[column].Trim() : string.Empty;
            }

            // The line number as the file has it, so a message about row 43 sends somebody
            // to row 43 of their spreadsheet rather than to the forty-third data row.
            rows.Add(new CsvRow(index + 1, cells));
        }

        return new CsvFile(header, rows, null);
    }

    /// <summary>
    /// Split the text into lines of fields, honouring quotes.
    /// </summary>
    /// <remarks>
    /// A character at a time rather than by splitting on commas, because a quoted field may
    /// contain a comma and a newline — and a client called "Smith, Jones and Partners" is
    /// the ordinary case rather than an edge one.
    /// </remarks>
    private static List<List<string>> Split(string text)
    {
        var lines = new List<List<string>>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (quoted)
            {
                if (character != '"')
                {
                    field.Append(character);
                    continue;
                }

                // Two quotes inside a quoted field is one literal quote, which is how the
                // format escapes them and how every spreadsheet writes them.
                if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                    continue;
                }

                quoted = false;
                continue;
            }

            switch (character)
            {
                case '"':
                    quoted = true;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    // Swallowed. A CRLF ending is what the format specifies and what Excel
                    // writes; treating the carriage return as content would put an invisible
                    // character at the end of every last field on every line.
                    break;

                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    lines.Add(fields);
                    fields = [];
                    break;

                default:
                    field.Append(character);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            lines.Add(fields);
        }

        return lines;
    }
}

/// <summary>A file that has been read, or the reason it could not be.</summary>
public sealed record CsvFile(
    IReadOnlyList<string> Columns, IReadOnlyList<CsvRow> Rows, string? Refusal)
{
    public bool IsReadable => Refusal is null;

    /// <summary>
    /// Are all of these columns present?
    /// </summary>
    /// <remarks>
    /// Checked before any row is looked at, so a file with the wrong shape is refused whole
    /// rather than producing four hundred identical complaints about a missing field.
    /// </remarks>
    public string? Missing(params string[] required) =>
        required.FirstOrDefault(column => !Columns.Contains(column, StringComparer.Ordinal));
}

/// <summary>One row, and where to find it in the file.</summary>
public sealed record CsvRow(int Line, IReadOnlyDictionary<string, string> Cells)
{
    /// <summary>A cell, or nothing when it is absent or blank.</summary>
    public string? this[string column] =>
        Cells.TryGetValue(column, out var value) && value.Length > 0 ? value : null;

    /// <summary>A cell read as a whole number, or nothing.</summary>
    /// <remarks>
    /// Nothing rather than zero when it will not parse, because zero is a legitimate value
    /// for most of the numbers being imported and a parse failure that became one would be
    /// invisible.
    /// </remarks>
    public int? Number(string column) =>
        int.TryParse(this[column], out var number) ? number : null;
}

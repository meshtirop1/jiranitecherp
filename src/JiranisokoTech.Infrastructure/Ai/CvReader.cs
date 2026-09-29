using System.IO.Compression;
using System.Text;
using System.Xml;
using JiranisokoTech.Application.Ai;
using JiranisokoTech.Application.Recruitment;

namespace JiranisokoTech.Infrastructure.Ai;

/// <summary>What could be made of a CV file.</summary>
/// <param name="Text">The words, when they could be pulled out here.</param>
/// <param name="Enclosure">The file itself, when the provider reads the format better than this code could.</param>
/// <param name="Note">Why neither, in a sentence for the page, or null when one of them is set.</param>
public sealed record CvContents(string? Text, Enclosure? Enclosure, string? Note)
{
    public bool IsReadable => Text is not null || Enclosure is not null;
}

/// <summary>
/// Reads a stored CV into something a model can be given.
/// </summary>
/// <remarks>
/// "Where readable" is the whole of the brief's promise, and this is where it is kept honest. A PDF
/// goes to the provider as a document, because it reads PDFs — layout, tables and all — far better
/// than anything that could be written here without a library. A Word or OpenDocument file is a zip
/// of XML, and its paragraphs are pulled out directly. Plain text is text. The old binary Word format
/// and RTF are not read at all, and the page says so, rather than summarising a CV from the
/// application form's fields and letting the person believe the CV was read.
/// </remarks>
public sealed class CvReader(ICvStore store)
{
    /// <summary>The most text sent from one CV. Past this it is a book, not a CV.</summary>
    private const int MostCharacters = 60_000;

    public async Task<CvContents> ReadAsync(
        string? storedName, string? fileName, CancellationToken cancellationToken = default)
    {
        if (storedName is null)
        {
            return new(null, null, "No CV was sent with this application.");
        }

        await using var stream = await store.OpenAsync(storedName, cancellationToken);

        if (stream is null)
        {
            return new(null, null, "The CV this application names is no longer on disk.");
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var extension = Path.GetExtension(fileName ?? storedName).ToLowerInvariant();

        try
        {
            return extension switch
            {
                ".pdf" => new(null, new Enclosure("application/pdf", bytes, "Curriculum vitae"), null),
                ".txt" => Words(Encoding.UTF8.GetString(bytes)),
                ".docx" => Words(FromZip(bytes, "word/document.xml", "p")),
                ".odt" => Words(FromZip(bytes, "content.xml", "p")),
                _ => new(null, null,
                    $"The CV is a {extension} file, which cannot be read here. Only its application "
                    + "form fields were used."),
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException)
        {
            return new(null, null, "The CV file is damaged or not what its name says, so it could not be read.");
        }
    }

    private static CvContents Words(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.Length == 0)
        {
            return new(null, null, "The CV has no text in it that could be read — it may be a scan.");
        }

        return new(trimmed.Length > MostCharacters ? trimmed[..MostCharacters] : trimmed, null, null);
    }

    /// <summary>
    /// Every text node in one XML part of a zip, with a line break after each paragraph.
    /// </summary>
    /// <remarks>
    /// Read with a plain reader and no DTD processing. The file is a stranger's upload, and an XML
    /// parser that resolves external entities is a way to make this server read its own files.
    /// </remarks>
    internal static string FromZip(byte[] bytes, string part, string paragraph)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var entry = archive.GetEntry(part) ?? throw new InvalidDataException($"No {part} in the file.");

        using var xml = XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });

        var text = new StringBuilder();

        while (xml.Read())
        {
            if (xml.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace)
            {
                text.Append(xml.Value);
            }
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == paragraph)
            {
                text.AppendLine();
            }
        }

        return text.ToString();
    }
}

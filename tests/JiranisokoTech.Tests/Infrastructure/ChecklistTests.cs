using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The checklist's headline agrees with its own tables.
/// </summary>
/// <remarks>
/// <b>Section 96 asks for one thing — that this file be kept live — and the file's own row admits
/// it was not.</b> The headline once read "58 done, 0 partial, 40 not started" and said nothing
/// was partial "for the first time"; recomputed from the tables by its own stated method it was
/// 60, 4 and 34. The engineering heading said "not started" and "none of it exists" over a block
/// that was entirely ✅. Twenty-one sections had no row at all.
///
/// All of that was corrected by hand, and the row that corrected it wrote down why it would happen
/// again: <em>nothing checks the headline against the tables, which is how it drifted — the count
/// is recomputed by hand</em>. It then drifted again within the week. Sections 48 and 63 were
/// built, committed and left marked ☐, and the headline went on naming the knowledge base and the
/// help desk as the last two not started while the CLI, internationalisation and IDE readiness had
/// never been touched.
///
/// So this is that check. A checklist whose rows drift behind the code is worth less than none,
/// because somebody reads it to decide what to build next — and the specific harm the second drift
/// would have done is that they would have built one of those two sections twice.
///
/// <see cref="RepositoryFactAttribute"/> because the image build leaves <c>docs/</c> out.
/// </remarks>
public partial class ChecklistTests
{
    /// <summary>
    /// Every section of the brief has a row.
    /// </summary>
    /// <remarks>
    /// Ninety-nine, because that is how many the brief has. A section with no row is one nobody has
    /// assessed and nobody can see is unassessed, which is the state twenty-one of them were in
    /// when the file's own row was written.
    ///
    /// More than one row is allowed and nineteen sections have one. The file is organised by area
    /// as the brief's example is, so a section that delivers several distinct things gets a row for
    /// each — section 36's AI is an assistant on one row and the recruitment aids on another, and
    /// reading them as one line would hide that they are in different states. <see cref="Status"/>
    /// is how a section with several rows gets one answer.
    /// </remarks>
    [RepositoryFact]
    public void Every_section_of_the_brief_has_a_row()
    {
        var assessed = Rows().Select(row => row.Section).ToHashSet();

        var missing = Enumerable.Range(1, Sections).Where(one => !assessed.Contains(one)).ToList();

        Assert.True(
            missing.Count == 0,
            "These sections of the brief have no row, so nobody has assessed them and nothing says "
            + "so: " + string.Join(", ", missing.Select(one => $"§{one}")));
    }

    /// <summary>
    /// <b>The headline's figures are the ones in the tables.</b>
    /// </summary>
    /// <remarks>
    /// The one that has failed twice in real life. Written as three numbers in words rather than
    /// digits because that is how the headline reads, and a headline that has to be edited to say
    /// "seventy-one" when a row turns ✅ is a headline somebody will notice is stale.
    ///
    /// Counted in sections rather than rows, because the headline speaks of the brief and the brief
    /// has ninety-nine sections — there are a hundred and thirty-five rows, and saying "a hundred
    /// and five done" would be true of the table and meaningless about the work.
    /// </remarks>
    [RepositoryFact]
    public void The_headline_says_the_number_the_tables_say()
    {
        var headline = Headline();
        var sections = Status();

        var done = sections.Count(one => one.Value == "✅");
        var partial = sections.Count(one => one.Value == "◐");
        var notStarted = sections.Count(one => one.Value == "☐");

        var wrong = new List<string>();

        foreach (var (count, said, what) in new[]
        {
            (done, InWords(done), "done"),
            (partial, InWords(partial), "partial"),
            (notStarted, InWords(notStarted), "not started"),
        })
        {
            if (!headline.Contains(said, StringComparison.OrdinalIgnoreCase))
            {
                wrong.Add($"  the tables say {count} {what}, and the headline does not say \"{said}\"");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "The honest headline and the tables disagree, which is the one thing section 96 asks "
            + "this file not to do — somebody reads the headline to decide what to build next:\n"
            + string.Join('\n', wrong)
            + "\n\nRecount and rewrite the paragraph under \"The honest headline\".");
    }

    /// <summary>
    /// The headline names every section that has not been started.
    /// </summary>
    /// <remarks>
    /// Because the drift that mattered was not a number. The headline named the knowledge base and
    /// the help desk as the last two sections not started, which was true of modules and false of
    /// the file — the CLI, internationalisation and IDE readiness had never been touched and went
    /// unmentioned. A count nobody can act on is worth less than a list, and a list is what
    /// somebody looking for the next job actually reads.
    ///
    /// Multi-tenancy is exempt: its row says it is excluded by agreement rather than outstanding,
    /// and naming it beside real work would make the list wrong in the other direction.
    /// </remarks>
    [RepositoryFact]
    public void The_headline_names_every_section_nobody_has_started()
    {
        var headline = Headline();

        var titles = Rows()
            .GroupBy(row => row.Section)
            .ToDictionary(group => group.Key, group => group.First().Title);

        var unnamed = Status()
            .Where(one => one.Value == "☐" && one.Key != ExcludedByAgreement)
            .Where(one => !headline.Contains($"§{one.Key}", StringComparison.Ordinal))
            .Select(one => $"  §{one.Key} {titles[one.Key]}")
            .Order()
            .ToList();

        Assert.True(
            unnamed.Count == 0,
            "These sections have not been started and the headline does not name them, so the one "
            + "person reading it to find the next job will not see them:\n"
            + string.Join('\n', unnamed));
    }

    /// <summary>
    /// A partial row says what the gap is.
    /// </summary>
    /// <remarks>
    /// The legend at the top promises it — "◐ partial, with the gap named" — and a ◐ with an empty
    /// note is the status that tells a reader nothing at all while looking like an assessment. It
    /// is also how a row goes stale without anybody noticing: there is no sentence to contradict.
    /// </remarks>
    [RepositoryFact]
    public void Every_partial_row_names_its_gap()
    {
        var silent = Rows()
            .Where(row => row.Mark == "◐" && row.Note.Trim().Length < 40)
            .Select(row => $"  §{row.Section} {row.Title}")
            .Order()
            .ToList();

        Assert.True(
            silent.Count == 0,
            "The legend says a partial row names its gap, and these do not — which leaves a status "
            + "that looks like an assessment and says nothing:\n" + string.Join('\n', silent));
    }

    /// <summary>How many sections the brief has.</summary>
    private const int Sections = 99;

    /// <summary>
    /// Multi-tenancy, which is out of scope by agreement rather than outstanding.
    /// </summary>
    /// <remarks>
    /// The one departure from the brief this project has agreed, recorded in CLAUDE.md and in the
    /// file's own opening. It carries ☐ because there is nothing built, and it must not be counted
    /// as work waiting to be done.
    /// </remarks>
    private const int ExcludedByAgreement = 3;

    private static readonly string[] Numbers =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
        "nineteen",
    ];

    private static readonly string[] Tens =
    [
        "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety",
    ];

    private sealed record Row(string Mark, int Section, string Title, string Note);

    /// <summary>
    /// One status per section of the brief, from however many rows it has.
    /// </summary>
    /// <remarks>
    /// The worst row wins, and that is the only honest rollup. A section with one thing built and
    /// one thing not is not done, and calling it done because two rows out of three say so is how a
    /// reader concludes there is nothing left to do in it. Nineteen sections have more than one
    /// row; section 36's two are in different states today.
    /// </remarks>
    private static Dictionary<int, string> Status()
    {
        var worst = new[] { "☐", "◐", "✅" };

        return Rows()
            .GroupBy(row => row.Section)
            .ToDictionary(
                group => group.Key,
                group => group.MinBy(row => Array.IndexOf(worst, row.Mark))!.Mark);
    }

    private static List<Row> Rows() =>
        [.. RowPattern()
            .Matches(File.ReadAllText(Checklist()))
            .Select(one => new Row(
                one.Groups["mark"].Value,
                int.Parse(one.Groups["section"].Value),
                one.Groups["title"].Value.Trim(),
                one.Groups["note"].Value))];

    /// <summary>
    /// The paragraph the file calls its honest headline.
    /// </summary>
    /// <remarks>
    /// Taken as everything between that heading and the next one, rather than as the first
    /// sentence, because the paragraph has been several sentences long every time it has drifted.
    /// </remarks>
    private static string Headline()
    {
        var text = File.ReadAllText(Checklist());

        var start = text.IndexOf("## The honest headline", StringComparison.Ordinal);

        Assert.True(start >= 0, "The checklist has no \"The honest headline\" section any more.");

        var next = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);

        return next < 0 ? text[start..] : text[start..next];
    }

    /// <summary>
    /// A number as the headline writes it.
    /// </summary>
    /// <remarks>
    /// Up to ninety-nine, which is as far as this file ever counts. Hyphenated above twenty
    /// because that is how the paragraph is written and how anybody would write it.
    /// </remarks>
    private static string InWords(int number)
    {
        Assert.InRange(number, 0, 99);

        if (number < 20)
        {
            return Numbers[number];
        }

        var tens = Tens[number / 10];

        return number % 10 == 0 ? tens : $"{tens}-{Numbers[number % 10]}";
    }

    private static string Checklist() =>
        Path.Combine(Root(), "docs", "implementation-checklist.md");

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "JiranisokoTech.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    /// <remarks>
    /// The note runs to the end of the line because several of them are a paragraph long and
    /// contain their own pipes inside code spans; taking everything after the title and trimming
    /// the trailing pipe is what survives that.
    /// </remarks>
    [GeneratedRegex(@"^\|\s*(?<mark>✅|◐|☐)\s*\|\s*(?<section>\d+)\s*\|(?<title>[^|]*)\|(?<note>.*)$",
        RegexOptions.Multiline)]
    private static partial Regex RowPattern();
}

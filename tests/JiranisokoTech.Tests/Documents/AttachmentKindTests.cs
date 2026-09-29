using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Infrastructure.Documents;
using JiranisokoTech.Tests.Infrastructure;

// Aliased because this test's own namespace ends in Documents, which shadows the static class.
using Rules = JiranisokoTech.Application.Documents.Documents;

namespace JiranisokoTech.Tests.Documents;

/// <summary>
/// Every kind of thing a file can be attached to is one the system can answer three questions
/// about.
/// </summary>
/// <remarks>
/// <b>This exists because nobody in this firm could upload a staff photograph, and the screen
/// blamed the file.</b>
///
/// <c>AttachedTo</c> has eight values and three switches over it: who may read one, who may
/// attach one, and does the thing it hangs off actually exist. The first two were complete. The
/// third — <c>AttachmentRepository.OwnerExistsAsync</c> — handled six of the eight and threw
/// <c>ArgumentOutOfRangeException</c> with the message "Unknown attachment kind." for
/// <c>Photo</c> and <c>Agreement</c>.
///
/// So the photograph form on your own profile and on anybody's staff record called
/// <c>AttachAsync</c>, which asks that question before it writes anything, and the page caught
/// the exception — both handlers catch <c>ArgumentException</c>, which is its base — and printed
/// the message as a refusal. Every attempt to add a face to the staff directory answered
/// "Unknown attachment kind. (Parameter 'kind')", which reads as a fault in the file somebody
/// just chose.
///
/// No test saw it, and the reason is worth writing down: the tests that cover photographs build
/// the row with <c>Attachment.Of</c> directly — see <c>RetentionSweepTests</c> — so they never
/// go through the service, and the service is where the question is asked.
///
/// A switch with a default that throws is the pattern this codebase uses everywhere and is the
/// right one: a new enum value should stop, not be silently mishandled. But it stops at run
/// time, on a page, in front of somebody. These three tests walk the enum so that it stops at
/// build time instead — which also means adding a ninth value cannot break the search box, and
/// that is not hypothetical: <c>SearchQueries</c> calls <c>PermissionToSee</c> for every value
/// of this enum on every search, so an unhandled kind takes the box down for everybody signed
/// in rather than only the page that added it.
/// </remarks>
public class AttachmentKindTests
{
    /// <summary>
    /// The owner of every kind can be looked for.
    /// </summary>
    /// <remarks>
    /// Against a real database and a identifier nothing owns, so the answer is false for every
    /// kind. False is fine; what is being asserted is that the question can be asked at all.
    /// </remarks>
    [Fact]
    public async Task Every_kind_can_be_asked_whether_the_thing_it_hangs_off_exists()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var repository = new AttachmentRepository(context);
        var refused = new List<string>();

        foreach (var kind in Enum.GetValues<AttachedTo>())
        {
            try
            {
                Assert.False(await repository.OwnerExistsAsync(kind, Guid.CreateVersion7()));
            }
            catch (ArgumentOutOfRangeException)
            {
                refused.Add(kind.ToString());
            }
        }

        Assert.True(
            refused.Count == 0,
            "AttachmentRepository.OwnerExistsAsync cannot say whether the owner of these kinds "
            + "exists, so nothing can ever be attached to one and the page blames the file: "
            + string.Join(", ", refused)
            + "\n\nAdd the arm. Every kind in this enum is reachable from a screen, or it would "
            + "not be in the enum.");
    }

    [Fact]
    public void Every_kind_has_a_permission_that_says_who_may_read_one()
    {
        var unanswered = Missing(Rules.PermissionToSee);

        Assert.True(
            unanswered.Count == 0,
            "Documents.PermissionToSee has no answer for these kinds, which takes the SEARCH BOX "
            + "down for every signed-in user — SearchQueries asks it for every value of this "
            + "enum: " + string.Join(", ", unanswered));
    }

    [Fact]
    public void Every_kind_has_a_permission_that_says_who_may_attach_one()
    {
        var unanswered = Missing(Rules.PermissionToAttach);

        Assert.True(
            unanswered.Count == 0,
            "Documents.PermissionToAttach has no answer for these kinds, so the upload form "
            + "cannot be gated and the page that shows it will not render: "
            + string.Join(", ", unanswered));
    }

    private static List<string> Missing(Func<AttachedTo, string> ask)
    {
        var unanswered = new List<string>();

        foreach (var kind in Enum.GetValues<AttachedTo>())
        {
            try
            {
                Assert.False(string.IsNullOrWhiteSpace(ask(kind)));
            }
            catch (ArgumentOutOfRangeException)
            {
                unanswered.Add(kind.ToString());
            }
        }

        return unanswered;
    }
}

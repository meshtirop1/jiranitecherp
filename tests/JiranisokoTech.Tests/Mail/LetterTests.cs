using JiranisokoTech.Application.Mail;

namespace JiranisokoTech.Tests.Mail;

/// <summary>
/// The words that go out, and what must never get into them.
/// </summary>
public class LetterTests
{
    private const string Link = "https://erp.jiranisokotech.co.ke/set-password?email=a%40b.co&token=xyz";

    [Fact]
    public void An_invitation_names_who_opened_the_account_and_carries_the_link()
    {
        var message = Letters.Invitation(
            "precious@jiranisokotech.co.ke", "Precious", Link, "Vincent Bungei");

        Assert.Equal("precious@jiranisokotech.co.ke", message.ToAddress);
        Assert.Contains("Precious", message.TextBody);

        // From a name they recognise, or they ignore it — or report it as
        // phishing, which is worse.
        Assert.Contains("Vincent Bungei", message.TextBody);
        Assert.Contains(Link, message.TextBody);
        Assert.Contains(Link.Replace("&", "&amp;"), message.HtmlBody);
    }

    /// <summary>
    /// Somebody who was not expecting an account needs telling what to do, and
    /// the wrong thing to do is follow the link.
    /// </summary>
    [Fact]
    public void An_invitation_says_what_to_do_if_it_was_not_expected()
    {
        var message = Letters.Invitation("a@b.co", "Somebody", Link, "Vincent Bungei");

        Assert.Contains("did not expect this", message.TextBody);
        Assert.Contains("do not follow the link", message.TextBody);
    }

    /// <summary>
    /// A display name is text somebody typed. A name of &lt;script&gt; in a
    /// message a colleague opens is the same injection as on a page, arriving
    /// somewhere nobody is looking for it.
    /// </summary>
    [Fact]
    public void A_name_cannot_carry_markup_into_the_html()
    {
        var message = Letters.Invitation(
            "a@b.co", "<script>alert(1)</script>", Link, "Vincent Bungei");

        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.Contains("&lt;script&gt;", message.HtmlBody);
    }

    [Fact]
    public void A_refusal_reason_cannot_carry_markup_either()
    {
        var message = Letters.Decided(
            "a@b.co", "Somebody", "requisition open", approved: false,
            because: "<img src=x onerror=alert(1)>", link: Link);

        Assert.DoesNotContain("<img", message.HtmlBody);
    }

    /// <summary>
    /// Both bodies, always. HTML only reads as markup in a client that will not
    /// render it; text only looks broken next to everything else in an inbox.
    /// </summary>
    [Fact]
    public void Every_message_has_both_a_text_and_an_html_body()
    {
        var messages = new[]
        {
            Letters.Invitation("a@b.co", "Somebody", Link, "Vincent"),
            Letters.AwaitingDecision("a@b.co", "Somebody", "requisition open", "Tirop", Link),
            Letters.Decided("a@b.co", "Somebody", "requisition open", true, null, Link),
        };

        Assert.All(messages, message =>
        {
            Assert.False(string.IsNullOrWhiteSpace(message.Subject));
            Assert.False(string.IsNullOrWhiteSpace(message.TextBody));
            Assert.Contains("<p>", message.HtmlBody);
        });
    }

    /// <summary>
    /// A message that arrives indented four spaces looks like a mistake. Raw
    /// string literals keep the indentation of the code they sit in.
    /// </summary>
    [Fact]
    public void Nothing_arrives_indented_by_its_own_source()
    {
        var message = Letters.Invitation("a@b.co", "Somebody", Link, "Vincent");

        Assert.All(
            message.TextBody.Split('\n'),
            line => Assert.False(line.StartsWith("    ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A refusal that does not say why makes somebody chase it. The reason goes
    /// in the message.
    /// </summary>
    [Fact]
    public void A_refusal_carries_its_reason_and_an_approval_needs_none()
    {
        var refused = Letters.Decided(
            "a@b.co", "Somebody", "requisition open", false, "no budget this quarter", Link);

        Assert.Contains("not approved", refused.TextBody);
        Assert.Contains("no budget this quarter", refused.TextBody);

        var approved = Letters.Decided(
            "a@b.co", "Somebody", "requisition open", true, null, Link);

        Assert.Contains("been approved", approved.TextBody);
        Assert.DoesNotContain("Reason given", approved.TextBody);
    }

    /// <summary>
    /// No remote images, and no tracking pixel. An image in a message reports
    /// back when it was opened and from where, which is not something this firm
    /// needs to know about its own staff.
    /// </summary>
    [Fact]
    public void Nothing_phones_home()
    {
        var message = Letters.AwaitingDecision(
            "a@b.co", "Somebody", "requisition open", "Tirop", Link);

        Assert.DoesNotContain("<img", message.HtmlBody);
    }

    /// <summary>
    /// The log line names the message, not its contents. A log carrying bodies
    /// is a second copy of everybody correspondence, kept somewhere with quite
    /// different access rules from the mailbox.
    /// </summary>
    [Fact]
    public void Describing_a_message_does_not_repeat_its_body()
    {
        var message = Letters.Invitation("a@b.co", "Somebody", Link, "Vincent");

        var described = message.ToString();

        Assert.Contains("a@b.co", described);
        Assert.DoesNotContain(Link, described);
    }
}

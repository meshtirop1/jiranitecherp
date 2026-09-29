using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Workflows;

/// <summary>
/// Signing in and pressing buttons, for the tests that walk a whole workflow.
/// </summary>
/// <remarks>
/// Every page test class carries its own copy of the sign-in; these share one because they
/// sign several different people in, one per step, which is the point of walking a chain as
/// the people who would actually do each step rather than as one administrator.
/// </remarks>
public static class Browsing
{
    public const string Password = "a-long-enough-password";

    public static async Task<HttpClient> SignedInAsync(
        ApplicationFactory factory, string email, params string[] roles)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);
            }

            var stored = (await users.FindByEmailAsync(email))!;

            foreach (var role in roles)
            {
                if (!await users.IsInRoleAsync(stored, role))
                {
                    await users.AddToRoleAsync(stored, role);
                }
            }
        }

        var browser = factory.CreateBrowser();
        var form = await browser.GetAsync("/sign-in");

        var signedIn = await browser.PostAsync("/sign-in", new FormUrlEncodedContent(HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = Password,
            })));

        // A sign-in that failed looks, several steps later, like a page that refused the
        // person — which sends somebody to read the wrong code.
        Assert.Equal(System.Net.HttpStatusCode.Redirect, signedIn.StatusCode);

        return browser;
    }

    /// <summary>
    /// Post the named form on a page, as the browser would, and return what came back.
    /// </summary>
    public static async Task<HttpResponseMessage> PressAsync(
        HttpClient browser, string path, string form,
        IReadOnlyDictionary<string, string>? values = null,
        IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        var page = await browser.GetAsync(path);
        var html = await page.Content.ReadAsStringAsync();

        Assert.True(
            html.Contains(form, StringComparison.Ordinal),
            $"{path} has no form called \"{form}\" for this person to press.");

        var fields = HtmlForm.Fill(html, values).ToList();

        fields.RemoveAll(field => field.Key == "_handler");
        fields.Add(new("_handler", form));

        if (extra is not null)
        {
            fields.AddRange(extra);
        }

        return await browser.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    /// <summary>
    /// The press was accepted: nothing on the page refused it. Otherwise say what it said.
    /// </summary>
    /// <remarks>
    /// Pages here answer a successful post two ways — a redirect, or the same page again with
    /// a confirmation — and a refused one with the page again and a reason in a refusal
    /// paragraph or beside a field. So the status cannot tell success from refusal, and
    /// asserting it reports "expected Found, was OK" with nothing about which rule refused.
    /// This fails on the page's own sentence instead.
    /// </remarks>
    public static void Accepted(HttpResponseMessage response)
    {
        // Moving on is acceptance. The body of a redirect is the page as it was drawn before
        // the handler decided to leave, standing notices and all, so it is not read.
        if (response.StatusCode == System.Net.HttpStatusCode.Found)
        {
            return;
        }

        var html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var refusal = System.Text.RegularExpressions.Regex.Match(
            html, "<(?:p|div) class=\"refusal\"[^>]*>(.*?)</(?:p|div)>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var invalid = System.Text.RegularExpressions.Regex.Matches(html, "<div class=\"validation-message\">([^<]*)</div>")
            .Select(match => match.Groups[1].Value)
            .ToList();

        if ((int)response.StatusCode < 400 && !refusal.Success && invalid.Count == 0)
        {
            return;
        }

        Assert.Fail($"{response.RequestMessage?.RequestUri} answered {(int)response.StatusCode}. "
            + $"Refusal: {(refusal.Success ? refusal.Groups[1].Value.Trim() : "none")}. "
            + $"Validation: {string.Join("; ", invalid)}");
    }
}

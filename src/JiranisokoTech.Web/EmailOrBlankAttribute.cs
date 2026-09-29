using System.ComponentModel.DataAnnotations;

namespace JiranisokoTech.Web;

/// <summary>
/// An email address, or nothing.
/// </summary>
/// <remarks>
/// <b>This exists because the framework's <c>EmailAddress</c> attribute calls an empty
/// string invalid.</b> It lets null through and refuses "", and a browser posts an empty text
/// box as "" rather than leaving the field out. So every optional email field in this application was, in fact,
/// required: leave the personal email empty on your profile and the form refused to save your
/// phone number, and because the forms show no message beside that box, it refused without
/// saying so. The same was true of a client's contact email and the firm's own address.
///
/// Blank — empty or only spaces — is accepted here, and anything else must look like an email
/// address. Where a field is genuinely required, <c>[Required]</c> sits beside this and refuses
/// the blank with its own message, which is the only place that refusal belongs.
/// <c>EmailAttributeTests</c> fails the build for that attribute anywhere in the web
/// project.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class EmailOrBlankAttribute : ValidationAttribute
{
    private static readonly EmailAddressAttribute Email = new();

    public EmailOrBlankAttribute()
        : base("That does not look like an email address.")
    {
    }

    public override bool IsValid(object? value) =>
        value is null
        || (value is string text && (string.IsNullOrWhiteSpace(text) || Email.IsValid(text.Trim())));
}

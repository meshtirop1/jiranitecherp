namespace JiranisokoTech.Application.Recruitment;

/// <summary>
/// What an applicant agrees to, word for word.
/// </summary>
/// <remarks>
/// Section 55's consent records. The careers form shows this sentence beside its checkbox and the
/// application stores this same sentence with the time it was agreed, so the record says what
/// was agreed rather than that something was. Changing the wording changes it for applications
/// from then on and leaves every earlier one holding the sentence its applicant actually saw.
/// </remarks>
public static class Consent
{
    public const string Careers =
        "I agree to Jiranisoko Tech Solutions using the details and CV in this application to "
        + "consider me for this role, and keeping them for as long as its published retention "
        + "period allows before they are erased.";
}

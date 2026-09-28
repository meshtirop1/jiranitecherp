namespace JiranisokoTech.Web;

/// <summary>
/// What the page says about the copy of the application it is running in.
/// </summary>
/// <remarks>
/// Section 86 asks for development, testing, staging and production, and the danger with more
/// than one copy is somebody doing real work in the wrong one — approving a real person's leave
/// on staging, or testing a pay run on the live system because the two look identical. So every
/// copy that is not production says what it is, on every page, above everything else.
/// Production says nothing: a banner people see all day stops being read, and the live system
/// is the one where nothing needs saying.
/// </remarks>
public static class EnvironmentBanner
{
    public static string? For(string environmentName) => environmentName switch
    {
        "Production" => null,
        "Staging" =>
            "Staging — a copy for trying changes before they go live. Nothing here is the firm's real record.",
        "Testing" => "Testing — an automated copy that is rebuilt for every run.",
        "Development" => "Development — running on a developer's machine.",
        _ => $"{environmentName} — not the live system.",
    };
}

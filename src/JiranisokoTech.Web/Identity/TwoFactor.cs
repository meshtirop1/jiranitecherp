using System.Text;
using System.Text.Encodings.Web;
using JiranisokoTech.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using QRCoder;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// Setting up and taking down a second factor.
/// </summary>
/// <remarks>
/// Time-based codes from an authenticator app, and nothing else. Not SMS: a
/// text message is delivered by whoever currently controls the phone number,
/// and a number is taken over with a phone call to a network's support desk. A
/// second factor that can be social-engineered away is one that adds a step
/// without adding a defence.
///
/// The secret never leaves this system in a form anybody has to keep. It is
/// shown once, during setup, and after that the only ways back in are the
/// authenticator itself or a recovery code.
/// </remarks>
public sealed class TwoFactor(
    UserManager<ApplicationUser> users,
    ApplicationSignInManager signInManager)
{
    /// <summary>
    /// How many recovery codes are issued.
    /// </summary>
    /// <remarks>
    /// Enough that losing one or two to a bad transcription does not matter,
    /// few enough that somebody actually keeps them somewhere rather than
    /// printing a page and losing it.
    /// </remarks>
    public const int RecoveryCodeCount = 8;

    private const string Issuer = "Jiranisoko Tech";

    /// <summary>
    /// The secret to enrol with, and the two ways of getting it into an app.
    /// </summary>
    /// <remarks>
    /// Keeps the existing key if there is one, and only makes a new key when
    /// there is none. This is the important half of the method.
    ///
    /// The first version reset on every call, which looked tidier and was
    /// wrong: mistyping the confirmation code sent somebody back here, a new
    /// key was generated, and the app they had just finished setting up was
    /// silently enrolled against a secret this system had already thrown away.
    /// Every code from then on would have been refused, with nothing on screen
    /// to explain why.
    ///
    /// Replacing a key is therefore something somebody asks for, by name, in
    /// <see cref="ResetKeyAsync"/>.
    /// </remarks>
    public async Task<Enrolment> BeginAsync(Guid userId)
    {
        var user = await Required(userId);

        var key = await users.GetAuthenticatorKeyAsync(user);

        if (string.IsNullOrWhiteSpace(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);

            key = await users.GetAuthenticatorKeyAsync(user)
                ?? throw new InvalidOperationException("Could not produce an authenticator key.");
        }

        var uri = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            UrlEncoder.Default.Encode(Issuer),
            UrlEncoder.Default.Encode(user.Email!),
            key);

        return new Enrolment(Readable(key), uri, QrSvg(uri));
    }

    /// <summary>
    /// Turn it on, if the code proves the app is set up.
    /// </summary>
    /// <remarks>
    /// The code is checked before two-factor is switched on, never after.
    /// Enabling first and verifying later leaves a window in which somebody is
    /// locked out of their own account by a setup that did not work.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ConfirmAsync(Guid userId, string code)
    {
        var user = await Required(userId);

        var verified = await users.VerifyTwoFactorTokenAsync(
            user, users.Options.Tokens.AuthenticatorTokenProvider, Clean(code));

        if (!verified)
        {
            throw new InvalidOperationException(
                "That code is not right. Check the clock on the phone is correct, and try the "
                + "code showing now.");
        }

        await users.SetTwoFactorEnabledAsync(user, true);

        /*
         * Recovery codes are issued here and shown once.
         *
         * Without them, a lost or wiped phone means an account nobody can get
         * into, recoverable only by an administrator turning the second factor
         * off — which is a support request at best and, for the only owner, a
         * system nobody can administer.
         */
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);

        return codes?.ToList() ?? [];
    }

    /// <summary>
    /// Throw the current secret away and start again.
    /// </summary>
    /// <remarks>
    /// For somebody who has lost the phone the app was on, or who got half way
    /// through and wants to begin afresh. Every authenticator entry against the
    /// old key stops working, which is the point.
    /// </remarks>
    public async Task<Enrolment> ResetKeyAsync(Guid userId)
    {
        var user = await Required(userId);

        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);

        return await BeginAsync(userId);
    }

    /// <summary>Issue a fresh set, invalidating the old ones.</summary>
    public async Task<IReadOnlyList<string>> NewRecoveryCodesAsync(Guid userId)
    {
        var user = await Required(userId);

        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);

        return codes?.ToList() ?? [];
    }

    public async Task<int> RecoveryCodesLeftAsync(Guid userId) =>
        await users.CountRecoveryCodesAsync(await Required(userId));

    public async Task<bool> IsOnAsync(Guid userId) =>
        await users.GetTwoFactorEnabledAsync(await Required(userId));

    /// <summary>
    /// Turn it off, and throw the key away with it.
    /// </summary>
    /// <remarks>
    /// The key is reset rather than kept. Leaving it in place means an old
    /// authenticator entry on a phone somebody no longer has still works the
    /// day two-factor is switched back on.
    /// </remarks>
    public async Task TurnOffAsync(Guid userId)
    {
        var user = await Required(userId);

        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
    }

    /// <summary>
    /// Finish a sign-in with a code from the authenticator.
    /// </summary>
    /// <remarks>
    /// <paramref name="rememberMachine"/> is offered but defaults to off. It
    /// writes a cookie that skips the second factor on that browser, which is a
    /// reasonable trade on somebody own laptop and a bad one on the machine in
    /// reception.
    /// </remarks>
    public Task<SignInResult> SignInWithCodeAsync(
        string code, bool rememberMe, bool rememberMachine) =>
        signInManager.TwoFactorAuthenticatorSignInAsync(
            Clean(code), rememberMe, rememberMachine);

    /// <summary>
    /// Finish a sign-in with a recovery code, which is then spent.
    /// </summary>
    /// <remarks>
    /// Single use, enforced by Identity. A recovery code that still worked
    /// afterwards would be a password written on a card.
    /// </remarks>
    public Task<SignInResult> SignInWithRecoveryCodeAsync(string code) =>
        signInManager.TwoFactorRecoveryCodeSignInAsync(Clean(code));

    /// <summary>Whoever is half way through signing in, if anybody.</summary>
    public Task<ApplicationUser?> WaitingOnSecondFactorAsync() =>
        signInManager.GetTwoFactorAuthenticationUserAsync();

    /// <summary>
    /// A code as people type it.
    /// </summary>
    /// <remarks>
    /// Authenticator apps show codes in two groups, recovery codes are usually
    /// written with a dash, and both get pasted with whitespace around them.
    /// Rejecting those is rejecting the code somebody is looking at.
    /// </remarks>
    private static string Clean(string code) =>
        code.Replace(" ", string.Empty).Replace("-", string.Empty).Trim();

    /// <summary>
    /// The key in groups of four, for typing in by hand.
    /// </summary>
    /// <remarks>
    /// Thirty-two characters in one run is a transcription error waiting to
    /// happen, and the manual path is the one somebody uses when the camera
    /// will not focus.
    /// </remarks>
    private static string Readable(string key)
    {
        var grouped = new StringBuilder();

        for (var at = 0; at < key.Length; at += 4)
        {
            grouped.Append(key.AsSpan(at, Math.Min(4, key.Length - at))).Append(' ');
        }

        return grouped.ToString().Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The enrolment URI as an inline SVG.
    /// </summary>
    /// <remarks>
    /// SVG rather than a PNG, so it is sharp at any size and needs no file
    /// written anywhere. Inline rather than a separate request, because a URL
    /// serving somebody authenticator secret is a URL that ends up in a proxy
    /// log.
    /// </remarks>
    private static string QrSvg(string uri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);

        return new SvgQRCode(data).GetGraphic(4);
    }

    private async Task<ApplicationUser> Required(Guid userId) =>
        await users.FindByIdAsync(userId.ToString())
        ?? throw new InvalidOperationException("There is no account with that identifier.");
}

/// <summary>What somebody needs to get an authenticator app set up.</summary>
public sealed record Enrolment(string Key, string Uri, string QrSvg);

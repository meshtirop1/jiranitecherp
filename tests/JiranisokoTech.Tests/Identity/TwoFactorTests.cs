using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// A second factor, from enrolment to the sign-in it gates.
/// </summary>
/// <remarks>
/// The codes here are generated the same way an authenticator app generates
/// them — from the shared secret and the clock — rather than stubbed. A stub
/// would prove the code path runs and nothing about whether the secret this
/// system hands out is one a real app could use.
/// </remarks>
public class TwoFactorTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task Enrolment_hands_out_a_secret_an_app_can_actually_use()
    {
        var user = await AccountAsync("enrol@jiranisokotech.co.ke");

        await factory.InScopeAsync(async services =>
        {
            var twoFactor = services.GetRequiredService<TwoFactor>();
            var setup = await twoFactor.BeginAsync(user);

            // The URI an app scans, in the shape apps expect.
            Assert.StartsWith("otpauth://totp/", setup.Uri);
            Assert.Contains("issuer=Jiranisoko", setup.Uri);
            // The address goes in unencoded: an at-sign is legal in the label
            // and every authenticator app expects to see it there.
            Assert.Contains("enrol@jiranisokotech.co.ke", setup.Uri);

            // The issuer has a space in it, which is not legal and is encoded.
            Assert.Contains("Jiranisoko%20Tech", setup.Uri);

            // A QR somebody can point a camera at, not a placeholder.
            Assert.Contains("<svg", setup.QrSvg);

            // And the manual fallback, grouped so it can be typed without
            // losing your place in thirty-two characters.
            Assert.Contains(' ', setup.Key);

            // The code an app would show right now is the one this accepts.
            var codes = await twoFactor.ConfirmAsync(user, CodeFor(setup.Key));

            Assert.Equal(TwoFactor.RecoveryCodeCount, codes.Count);
            Assert.True(await twoFactor.IsOnAsync(user));
        });
    }

    /// <summary>
    /// Enabling first and verifying later leaves somebody locked out of their
    /// own account by a setup that did not work.
    /// </summary>
    [Fact]
    public async Task A_wrong_code_does_not_turn_it_on()
    {
        var user = await AccountAsync("wrongcode@jiranisokotech.co.ke");

        await factory.InScopeAsync(async services =>
        {
            var twoFactor = services.GetRequiredService<TwoFactor>();

            await twoFactor.BeginAsync(user);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => twoFactor.ConfirmAsync(user, "000000"));

            Assert.False(await twoFactor.IsOnAsync(user));
        });
    }

    /// <summary>
    /// The bug this test exists for.
    /// </summary>
    /// <remarks>
    /// The first version of BeginAsync reset the key on every call. Mistyping
    /// the confirmation code sent somebody back to the setup page, a new key was
    /// made, and the app they had just finished configuring was enrolled against
    /// a secret already thrown away — every code refused from then on, with
    /// nothing on screen to explain why.
    /// </remarks>
    [Fact]
    public async Task Asking_for_the_setup_again_does_not_change_the_secret()
    {
        var user = await AccountAsync("stable@jiranisokotech.co.ke");

        await factory.InScopeAsync(async services =>
        {
            var twoFactor = services.GetRequiredService<TwoFactor>();

            var first = await twoFactor.BeginAsync(user);
            var second = await twoFactor.BeginAsync(user);

            Assert.Equal(first.Key, second.Key);

            // And an app set up from the first still works.
            await twoFactor.ConfirmAsync(user, CodeFor(first.Key));

            Assert.True(await twoFactor.IsOnAsync(user));
        });
    }

    /// <summary>
    /// Replacing a key is something somebody asks for by name, and then the old
    /// authenticator entry stops working — which is the point of asking.
    /// </summary>
    [Fact]
    public async Task Starting_over_does_change_the_secret()
    {
        var user = await AccountAsync("startover@jiranisokotech.co.ke");

        await factory.InScopeAsync(async services =>
        {
            var twoFactor = services.GetRequiredService<TwoFactor>();

            var first = await twoFactor.BeginAsync(user);
            var second = await twoFactor.ResetKeyAsync(user);

            Assert.NotEqual(first.Key, second.Key);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => twoFactor.ConfirmAsync(user, CodeFor(first.Key)));
        });
    }

    /// <summary>
    /// The whole reason for the module: a correct password is no longer enough.
    /// </summary>
    [Fact]
    public async Task A_password_alone_no_longer_gets_somebody_in()
    {
        var user = await AccountAsync("gated@jiranisokotech.co.ke");

        await EnableAsync(user);

        Assert.Equal(
            SignInOutcome.SecondFactorRequired,
            await AttemptAsync("gated@jiranisokotech.co.ke"));
    }

    [Fact]
    public async Task Turning_it_off_lets_a_password_through_again()
    {
        var user = await AccountAsync("ungated@jiranisokotech.co.ke");

        await EnableAsync(user);

        await factory.InScopeAsync(services =>
            services.GetRequiredService<TwoFactor>().TurnOffAsync(user));

        Assert.Equal(
            SignInOutcome.Succeeded,
            await AttemptAsync("ungated@jiranisokotech.co.ke"));
    }

    /// <summary>
    /// Without recovery codes, a lost phone is an account nobody can get into —
    /// and for the only owner, a system nobody can administer.
    /// </summary>
    [Fact]
    public async Task Recovery_codes_are_issued_and_counted_down()
    {
        var user = await AccountAsync("recovery@jiranisokotech.co.ke");

        var codes = await EnableAsync(user);

        await factory.InScopeAsync(async services =>
        {
            var twoFactor = services.GetRequiredService<TwoFactor>();

            Assert.Equal(TwoFactor.RecoveryCodeCount, await twoFactor.RecoveryCodesLeftAsync(user));

            // A fresh set replaces the old one rather than adding to it.
            var again = await twoFactor.NewRecoveryCodesAsync(user);

            Assert.Equal(TwoFactor.RecoveryCodeCount, again.Count);
            Assert.Empty(again.Intersect(codes));
        });
    }

    /// <summary>
    /// Turning it off throws the key away too, so an old entry on a phone
    /// somebody no longer has does not start working again the day it is
    /// switched back on.
    /// </summary>
    [Fact]
    public async Task Turning_it_off_throws_the_key_away()
    {
        var user = await AccountAsync("keythrown@jiranisokotech.co.ke");

        await factory.InScopeAsync(async services =>
        {
            var twoFactor = services.GetRequiredService<TwoFactor>();

            var before = await twoFactor.BeginAsync(user);
            await twoFactor.ConfirmAsync(user, CodeFor(before.Key));

            await twoFactor.TurnOffAsync(user);

            var after = await twoFactor.BeginAsync(user);

            Assert.NotEqual(before.Key, after.Key);
        });
    }

    /// <summary>
    /// Authenticator apps show codes in two groups and people paste them with
    /// spaces. Refusing those is refusing the code somebody is looking at.
    /// </summary>
    [Fact]
    public async Task A_code_typed_with_spaces_is_accepted()
    {
        var user = await AccountAsync("spaced@jiranisokotech.co.ke");

        await factory.InScopeAsync(async services =>
        {
            var twoFactor = services.GetRequiredService<TwoFactor>();
            var setup = await twoFactor.BeginAsync(user);

            var code = CodeFor(setup.Key);
            var spaced = $" {code[..3]} {code[3..]} ";

            await twoFactor.ConfirmAsync(user, spaced);

            Assert.True(await twoFactor.IsOnAsync(user));
        });
    }

    /// <summary>
    /// The code an authenticator app would be showing for this secret now.
    /// </summary>
    /// <remarks>
    /// The key is displayed in groups of four for typing, so the spaces come out
    /// again before it is decoded.
    /// </remarks>
    private static string CodeFor(string readableKey) =>
        new Totp(Base32Encoding.ToBytes(readableKey.Replace(" ", string.Empty).ToUpperInvariant()))
            .ComputeTotp();

    private async Task<Guid> AccountAsync(string email)
    {
        var user = await factory.CreateAccountAsync(email, Password, email);

        return user.Id;
    }

    private async Task<IReadOnlyList<string>> EnableAsync(Guid user)
    {
        IReadOnlyList<string> codes = [];

        await factory.InScopeAsync(async services =>
        {
            var twoFactor = services.GetRequiredService<TwoFactor>();
            var setup = await twoFactor.BeginAsync(user);

            codes = await twoFactor.ConfirmAsync(user, CodeFor(setup.Key));
        });

        return codes;
    }

    private Task<SignInOutcome> AttemptAsync(string email) =>
        factory.InRequestAsync(services =>
            services.GetRequiredService<SignInService>().PasswordSignInAsync(
                email, Password, remember: false, ipAddress: null, userAgent: null));
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.IntegrationTests;

public class TwoFactorTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private sealed record Staff(string Email, string Password, Guid Id, string Key);

    /// <summary>The six-digit code an authenticator app would show now (RFC 6238, 30-second steps, SHA-1).</summary>
    private static string CodeFor(string base32Key, int stepOffset = 0)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32Key.ToUpperInvariant().Where(alphabet.Contains).Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + stepOffset;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0xF;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private async Task<T> SecurityAsync<T>(Func<IStaffSecurityService, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IStaffSecurityService>());
    }

    private async Task SecurityAsync(Func<IStaffSecurityService, Task> action) =>
        await SecurityAsync(async s => { await action(s); return true; });

    /// <summary>A member of staff who has already turned two-factor on.</summary>
    private async Task<(Staff Staff, IReadOnlyList<string> Recovery)> StaffWithTwoFactorAsync()
    {
        var made = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        await SecurityAsync(s => s.BeginSetupAsync(made.UserId));
        string key;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaCommerceUser>>();
            key = (await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(made.UserId.ToString()))!))!;
        }
        var recovery = await SecurityAsync(s => s.ConfirmSetupAsync(made.UserId, CodeFor(key)));
        return (new Staff(made.Email, made.Password, made.UserId, key), recovery);
    }

    private Task<HttpResponseMessage> TokenAsync(string email, string password, string? code = null) =>
        factory.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new { email, password, code });

    [Fact]
    public async Task Setup_needs_a_right_code_then_gives_ten_recovery_codes()
    {
        var made = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        var setup = await SecurityAsync(s => s.BeginSetupAsync(made.UserId));
        Assert.StartsWith("otpauth://totp/", setup.Uri);
        Assert.False((await SecurityAsync(s => s.GetStatusAsync(made.UserId))).Enabled);

        await Assert.ThrowsAsync<PiranhaCommerceValidationException>(() => SecurityAsync(s => s.ConfirmSetupAsync(made.UserId, "000000x")));
        Assert.False((await SecurityAsync(s => s.GetStatusAsync(made.UserId))).Enabled); // a wrong code turns nothing on

        var key = Regex.Match(setup.Uri, "secret=([A-Z2-7]+)").Groups[1].Value;
        var codes = await SecurityAsync(s => s.ConfirmSetupAsync(made.UserId, CodeFor(key)));
        Assert.Equal(10, codes.Count);
        Assert.Equal(new TwoFactorStatus(true, 10), await SecurityAsync(s => s.GetStatusAsync(made.UserId)));
    }

    [Fact]
    public async Task The_api_asks_for_the_code_and_accepts_the_right_one()
    {
        var (staff, _) = await StaffWithTwoFactorAsync();

        var without = await TokenAsync(staff.Email, staff.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        Assert.Contains("two_factor_required", await without.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(staff.Email, staff.Password, "123456" == CodeFor(staff.Key) ? "654321" : "123456")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(staff.Email, "wrong password 1!", CodeFor(staff.Key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(staff.Email, staff.Password, CodeFor(staff.Key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(staff.Email, staff.Password, CodeFor(staff.Key).Insert(3, " "))).StatusCode); // a space is fine
    }

    [Fact]
    public async Task A_recovery_code_works_once()
    {
        var (staff, recovery) = await StaffWithTwoFactorAsync();
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(staff.Email, staff.Password, recovery[0])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(staff.Email, staff.Password, recovery[0])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(staff.Email, staff.Password, recovery[1])).StatusCode);
        Assert.Equal(8, (await SecurityAsync(s => s.GetStatusAsync(staff.Id))).RecoveryCodesLeft);
    }

    [Fact]
    public async Task Guessing_codes_locks_the_account_even_if_the_password_is_sent_right_in_between()
    {
        var (staff, _) = await StaffWithTwoFactorAsync();
        var wrong = CodeFor(staff.Key) == "111111" ? "222222" : "111111";
        for (var i = 0; i < 3; i++) await TokenAsync(staff.Email, staff.Password, wrong);
        // A right password with no code must not wipe the count of wrong guesses.
        await TokenAsync(staff.Email, staff.Password);
        await TokenAsync(staff.Email, staff.Password, wrong);
        await TokenAsync(staff.Email, staff.Password, wrong);
        // Locked now: even the right code is refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(staff.Email, staff.Password, CodeFor(staff.Key))).StatusCode);
    }

    [Fact]
    public async Task The_admin_sign_in_asks_for_the_code_as_a_second_step()
    {
        var (staff, _) = await StaffWithTwoFactorAsync();
        var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

        async Task<HttpResponseMessage> Post(string? code)
        {
            var form = await (await browser.GetAsync("/admin/login")).Content.ReadAsStringAsync();
            var token = Regex.Match(form, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            var fields = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token, ["_handler"] = "login", ["Input.Email"] = staff.Email, ["Input.Password"] = staff.Password,
            };
            if (code is not null) fields["Input.Code"] = code;
            return await browser.PostAsync("/admin/login", new FormUrlEncodedContent(fields));
        }

        var first = await Post(null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); // no sign-in yet: the page asks for the code
        var html = await first.Content.ReadAsStringAsync();
        Assert.Contains("authenticator app", html);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/admin")).StatusCode); // still signed out

        var wrong = await Post("000000");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("That code is not right", await wrong.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Redirect, (await Post(CodeFor(staff.Key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Turning_it_off_and_replacing_recovery_codes_need_a_current_code()
    {
        var (staff, _) = await StaffWithTwoFactorAsync();
        await Assert.ThrowsAsync<PiranhaCommerceValidationException>(() => SecurityAsync(s => s.DisableAsync(staff.Id, "nope")));
        await Assert.ThrowsAsync<PiranhaCommerceValidationException>(() => SecurityAsync(s => s.NewRecoveryCodesAsync(staff.Id, "nope")));

        var fresh = await SecurityAsync(s => s.NewRecoveryCodesAsync(staff.Id, CodeFor(staff.Key)));
        Assert.Equal(10, fresh.Count);

        await SecurityAsync(s => s.DisableAsync(staff.Id, CodeFor(staff.Key)));
        Assert.False((await SecurityAsync(s => s.GetStatusAsync(staff.Id))).Enabled);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(staff.Email, staff.Password)).StatusCode); // password alone again
    }

    [Fact]
    public async Task An_administrator_can_reset_a_lost_phone_and_the_old_codes_stop_working()
    {
        var (staff, recovery) = await StaffWithTwoFactorAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var reset = await scope.ServiceProvider.GetRequiredService<IStaffAdminService>().ResetTwoFactorAsync(staff.Id);
            Assert.False(reset!.TwoFactorEnabled);
        }
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(staff.Email, staff.Password)).StatusCode);

        // Setting it up again gives a new key: the old key and the old recovery codes do nothing.
        var setup = await SecurityAsync(s => s.BeginSetupAsync(staff.Id));
        var newKey = Regex.Match(setup.Uri, "secret=([A-Z2-7]+)").Groups[1].Value;
        Assert.NotEqual(staff.Key, newKey);
        await SecurityAsync(s => s.ConfirmSetupAsync(staff.Id, CodeFor(newKey)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(staff.Email, staff.Password, recovery[2])).StatusCode);
    }

    [Fact]
    public async Task Someone_who_has_not_set_it_up_signs_in_as_before()
    {
        var made = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(made.Email, made.Password)).StatusCode);
    }
}

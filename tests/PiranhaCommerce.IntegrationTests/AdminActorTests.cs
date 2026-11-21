using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using PiranhaCommerce.Admin.Services;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Admin changes are recorded against the signed-in user ("edited by", "resolved by", the payment ledger).</summary>
public class AdminActorTests
{
    private sealed class FakeAuth(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));

    [Fact]
    public async Task The_actor_is_the_signed_in_users_email_falling_back_to_their_name()
    {
        Assert.Equal("ada@example.com", await new FakeAuth(User(new Claim("email", "ada@example.com"), new Claim(ClaimTypes.Name, "Ada"))).GetActorAsync());
        Assert.Equal("ada@example.com", await new FakeAuth(User(new Claim(ClaimTypes.Email, "ada@example.com"))).GetActorAsync());
        Assert.Equal("Ada", await new FakeAuth(User(new Claim(ClaimTypes.Name, "Ada"))).GetActorAsync());
    }

    [Fact]
    public async Task Nobody_signed_in_is_no_actor_rather_than_a_made_up_one()
    {
        Assert.Null(await new FakeAuth(new ClaimsPrincipal(new ClaimsIdentity())).GetActorAsync());
    }
}

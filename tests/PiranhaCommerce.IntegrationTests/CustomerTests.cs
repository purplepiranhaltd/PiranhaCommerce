using System.Net;
using System.Net.Http.Json;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;

namespace PiranhaCommerce.IntegrationTests;

public class CustomerTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static CustomerInput Input(string? email = null, string? first = "Ada", string? last = null) =>
        new() { Email = email ?? $"c-{Tag()}@example.com", FirstName = first, LastName = last ?? "Lovelace-" + Tag() };

    private static async Task<CustomerDto> Create(HttpClient c, CustomerInput input)
    {
        var r = await c.PostAsJsonAsync("/api/v1/customers", input);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<CustomerDto>())!;
    }

    private static AddressInput Address(string? label = "Home") =>
        new() { Label = label, Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1 1AA", CountryCode = "gb" };

    [Fact]
    public async Task Create_get_update_delete_roundtrip()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var input = Input();
        input.Phone = "0113 000";
        input.Notes = "Prefers email";

        var created = await c.PostAsJsonAsync("/api/v1/customers", input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var customer = (await created.Content.ReadFromJsonAsync<CustomerDto>())!;
        Assert.Equal($"/api/v1/customers/{customer.Id}", created.Headers.Location?.ToString());
        Assert.True(customer.Active);
        Assert.Null(customer.UpdatedUtc);

        var fetched = (await c.GetFromJsonAsync<CustomerDto>($"/api/v1/customers/{customer.Id}"))!;
        Assert.Equal(input.Email, fetched.Email);
        Assert.Equal("Prefers email", fetched.Notes);

        input.FirstName = "Augusta";
        input.Active = false;
        var updated = (await (await c.PutAsJsonAsync($"/api/v1/customers/{customer.Id}", input)).Content.ReadFromJsonAsync<CustomerDto>())!;
        Assert.Equal("Augusta", updated.FirstName);
        Assert.False(updated.Active);
        Assert.NotNull(updated.UpdatedUtc);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/customers/{customer.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/customers/{customer.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/v1/customers/{customer.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync($"/api/v1/customers/{Guid.NewGuid()}", Input())).StatusCode);
    }

    [Fact]
    public async Task Email_is_required_valid_and_unique_whatever_the_letter_case()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var email = $"Unique-{Tag()}@Example.com";
        var first = await Create(c, Input(email));

        var clash = await c.PostAsJsonAsync("/api/v1/customers", Input(email.ToLowerInvariant()));
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        Assert.Contains("Email", await clash.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/customers", Input("not-an-email"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/customers", Input(""))).StatusCode);

        // changing to someone else's email is refused; keeping your own (even re-cased) is fine
        var other = await Create(c, Input());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/v1/customers/{other.Id}", Input(email))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/customers/{first.Id}", Input(email.ToUpperInvariant()))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_signups_with_one_email_let_exactly_one_win()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var email = $"race-{Tag()}@example.com";

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => c.PostAsJsonAsync("/api/v1/customers", Input(email))));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.BadRequest)); // friendly error, never a 500
    }

    [Fact]
    public async Task Search_and_paging()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var tag = Tag();
        for (var i = 0; i < 3; i++) await Create(c, Input(email: $"page{i}-{tag}@example.com", last: "Findable" + tag));
        await Create(c, Input());

        var page1 = (await c.GetFromJsonAsync<PagedResult<CustomerDto>>($"/api/v1/customers?search={tag.ToUpperInvariant()}&pageSize=2"))!;
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        var page2 = (await c.GetFromJsonAsync<PagedResult<CustomerDto>>($"/api/v1/customers?search={tag}&pageSize=2&page=2"))!;
        Assert.Single(page2.Items);
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page2.Items.Select(i => i.Id))); // stable, no overlap
    }

    [Fact]
    public async Task Address_book_roundtrip_with_normalised_country_and_ownership_checks()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var ada = await Create(c, Input());
        var bob = await Create(c, Input());

        var added = await c.PostAsJsonAsync($"/api/v1/customers/{ada.Id}/addresses", Address());
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var address = (await added.Content.ReadFromJsonAsync<AddressDto>())!;
        Assert.Equal("GB", address.CountryCode);                 // stored upper-case
        Assert.Equal(ada.Id, address.CustomerId);

        var changed = Address("Work");
        changed.City = "York";
        var put = (await (await c.PutAsJsonAsync($"/api/v1/customers/{ada.Id}/addresses/{address.Id}", changed)).Content.ReadFromJsonAsync<AddressDto>())!;
        Assert.Equal("York", put.City);
        Assert.Equal("Work", put.Label);

        var list = (await c.GetFromJsonAsync<List<AddressDto>>($"/api/v1/customers/{ada.Id}/addresses"))!;
        Assert.Single(list);

        // another customer's address cannot be read or changed through the wrong customer
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync($"/api/v1/customers/{bob.Id}/addresses/{address.Id}", Address())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/v1/customers/{bob.Id}/addresses/{address.Id}")).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<List<AddressDto>>($"/api/v1/customers/{bob.Id}/addresses"))!);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/customers/{ada.Id}/addresses/{address.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/customers/{Guid.NewGuid()}/addresses")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"/api/v1/customers/{Guid.NewGuid()}/addresses", Address())).StatusCode);
    }

    [Fact]
    public async Task Address_validation_and_limit()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var ada = await Create(c, Input());
        var url = $"/api/v1/customers/{ada.Id}/addresses";

        var noCity = Address(); noCity.City = "";
        var badCountry = Address(); badCountry.CountryCode = "GBR";
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url, noCity)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url, badCountry)).StatusCode);

        for (var i = 0; i < 50; i++) Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(url, Address("a" + i))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url, Address("one too many"))).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_customer_removes_their_addresses()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var ada = await Create(c, Input());
        await c.PostAsJsonAsync($"/api/v1/customers/{ada.Id}/addresses", Address());

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/customers/{ada.Id}")).StatusCode); // FK cascade, no 409/500
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/customers/{ada.Id}/addresses")).StatusCode);
    }

    [Fact]
    public async Task Read_only_users_can_read_but_not_change_customers()
    {
        var admin = await factory.CreateAuthenticatedApiClientAsync();
        var ada = await Create(admin, Input());
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/customers/{ada.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/customers", Input())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"/api/v1/customers/{ada.Id}", Input())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/v1/customers/{ada.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/v1/customers/{ada.Id}/addresses", Address())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/api/v1/customers")).StatusCode);
    }

    [Fact]
    public void Display_name_falls_back_to_the_email()
    {
        CustomerDto C(string? f, string? l) => new(Guid.NewGuid(), "a@b.com", f, l, null, null, true, default, null);
        Assert.Equal("Ada Lovelace", C("Ada", "Lovelace").DisplayName);
        Assert.Equal("Lovelace", C(null, "Lovelace").DisplayName);
        Assert.Equal("a@b.com", C(" ", null).DisplayName);
    }
}

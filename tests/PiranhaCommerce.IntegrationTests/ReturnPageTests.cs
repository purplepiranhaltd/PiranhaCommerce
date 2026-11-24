using System.Net;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The pages Stripe (or any gateway) sends the shopper back to.</summary>
public class ReturnPageTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    [Theory]
    [InlineData("/thanks", "Thank you")]
    [InlineData("/cart", "Payment cancelled")]
    public async Task A_shopper_returning_from_the_gateway_sees_a_page_without_signing_in(string path, string heading)
    {
        var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"<h1>{heading}</h1>", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/thanks")]
    [InlineData("/cart")]
    public async Task Nothing_from_the_address_is_echoed_into_the_page(string path)
    {
        var body = await (await factory.CreateClient().GetAsync(path + "?session=cs_test_%3Cscript%3Ealert(1)%3C/script%3E&x=%22%3E%3Cimg%20src=x%3E")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>alert", body);
        Assert.DoesNotContain("cs_test_", body);
        Assert.DoesNotContain("<img src=x>", body);
    }
}

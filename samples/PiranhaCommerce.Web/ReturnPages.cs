namespace PiranhaCommerce.Web;

/// <summary>
/// The two pages a payment gateway sends a shopper back to: <c>/thanks</c> after paying and <c>/cart</c> if they back out. The sample has
/// no storefront, so these are deliberately plain; a real shop points <c>Piranha:Stripe:SuccessUrl</c> and <c>CancelUrl</c> at its own pages.
///
/// They are fixed text on purpose. The gateway adds query values (a session id) to the address, and echoing anything from the address
/// into the page is how a crafted link becomes script injection. They also say nothing about the order, because the browser here has no
/// signed-in shopper: the order is confirmed by the gateway's webhook, not by this page being opened.
/// </summary>
internal static class ReturnPages
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/thanks", () => Page("Thank you",
            "Thank you. Your payment is being confirmed with the card provider.",
            "Your order will show as paid as soon as the confirmation arrives, which is usually within seconds."));

        app.MapGet("/cart", () => Page("Payment cancelled",
            "Your payment was cancelled and you have not been charged.",
            "Your order is still open and unpaid. You can try paying again."));
    }

    private static IResult Page(string title, string headline, string detail) => Results.Content(
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta name="robots" content="noindex">
          <title>{{title}} - Piranha Commerce sample</title>
          <style>body{font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem;line-height:1.5}</style>
        </head>
        <body>
          <h1>{{title}}</h1>
          <p>{{headline}}</p>
          <p>{{detail}}</p>
          <p><small>This is a placeholder page from the sample app. Point your shop's own pages at the payment gateway instead.</small></p>
        </body>
        </html>
        """, "text/html; charset=utf-8");
}

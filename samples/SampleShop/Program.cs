// The sample shop plays both sides of the phase 01 demo.
//
//   SAMPLESHOP_VARIANT=legacy     the system being migrated away from
//   SAMPLESHOP_VARIANT=candidate  the "migrated" version
//
// The candidate differs from the legacy side in exactly the ways a real migration does, so
// every class of the verdict has something to find:
//   - the product list is sorted with the current culture; run the legacy side with
//     DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 and the order changes although the code does
//     not — the NLS → ICU trap of a .NET Framework migration, reproduced on Linux;
//   - line totals come out of double arithmetic (noise a tolerance absorbs);
//   - a coupon larger than the cart drives the candidate's total below zero (a regression);
//   - checkout with an empty cart crashes the legacy side and is refused cleanly by the
//     candidate (a fix candidate);
//   - order ids and timestamps are new on every run (masks absorb them).
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();

// Read through configuration, so environment variables and test hosts set them the same way.
var variant = app.Configuration["SAMPLESHOP_VARIANT"] ?? "legacy";
var isCandidate = string.Equals(variant, "candidate", StringComparison.OrdinalIgnoreCase);
var allowReset = app.Configuration["SAMPLESHOP_ALLOW_RESET"] == "1";

var catalog = new[]
{
    new Product("ZEB-03", "Zebra mug", 7.25m),
    new Product("APL-01", "apple pie", 12.50m),
    new Product("ECL-02", "éclair box", 19.99m),
    new Product("ANG-04", "Ångström ruler", 3.10m),
};
var coupons = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["WELCOME5"] = 5m, ["BIGSALE"] = 150m };
var carts = new ConcurrentDictionary<string, Cart>(StringComparer.Ordinal);

Cart CartOf(HttpContext context)
{
    if (!context.Request.Cookies.TryGetValue("sk_session", out var id) || string.IsNullOrEmpty(id))
    {
        id = Guid.NewGuid().ToString("N");
        context.Response.Cookies.Append("sk_session", id, new CookieOptions { HttpOnly = true, Path = "/" });
    }

    return carts.GetOrAdd(id, _ => new Cart());
}

object CartJson(Cart cart)
{
    lock (cart)
    {
        var lines = cart.Lines.Select(l => new
        {
            sku = l.Product.Sku,
            quantity = l.Quantity,
            lineTotal = isCandidate ? (object)((double)l.Product.Price * l.Quantity) : l.Product.Price * l.Quantity,
        }).ToList();
        var subtotal = cart.Lines.Sum(l => l.Product.Price * l.Quantity);
        var discount = isCandidate ? cart.Discount : Math.Min(cart.Discount, subtotal);
        return new { items = lines, discount, total = subtotal - discount };
    }
}

app.MapGet("/products", (string? sort) =>
{
    IEnumerable<Product> items = catalog;
    if (sort == "name")
    {
        items = items.OrderBy(p => p.Name, StringComparer.CurrentCulture);
    }

    return Results.Json(new
    {
        items = items.Select(p => new { sku = p.Sku, name = p.Name, price = p.Price }),
        generatedAt = DateTimeOffset.UtcNow,
    });
});

app.MapPost("/cart/items", async (HttpContext context) =>
{
    var request = await context.Request.ReadFromJsonAsync<AddItem>();
    var product = catalog.FirstOrDefault(p => p.Sku == request?.Sku);
    if (product is null || request!.Quantity <= 0)
    {
        return Results.Json(new { error = "unknown-product" }, statusCode: 400);
    }

    var cart = CartOf(context);
    lock (cart)
    {
        cart.Lines.Add(new Line(product, request.Quantity));
    }

    return Results.Json(CartJson(cart));
});

app.MapPost("/cart/coupon", async (HttpContext context) =>
{
    var request = await context.Request.ReadFromJsonAsync<Coupon>();
    if (request?.Code is null || !coupons.TryGetValue(request.Code, out var amount))
    {
        return Results.Json(new { error = "unknown-coupon" }, statusCode: 400);
    }

    var cart = CartOf(context);
    lock (cart)
    {
        cart.Discount += amount;
    }

    return Results.Json(CartJson(cart));
});

app.MapGet("/cart", (HttpContext context) =>
{
    var cart = CartOf(context);
    var html = new StringBuilder("<!doctype html><html><head><title>Cart</title></head><body><table class=\"lines\">");
    decimal total;
    lock (cart)
    {
        foreach (var line in cart.Lines)
        {
            var lineTotal = line.Product.Price * line.Quantity;
            html.Append(CultureInfo.InvariantCulture, $"<tr data-sku=\"{line.Product.Sku}\"><td class=\"qty\">{line.Quantity}</td><td class=\"line-total\">{lineTotal.ToString("0.00", CultureInfo.InvariantCulture)}</td></tr>");
        }

        var subtotal = cart.Lines.Sum(l => l.Product.Price * l.Quantity);
        total = subtotal - (isCandidate ? cart.Discount : Math.Min(cart.Discount, subtotal));
    }

    // The legacy page carries a server-generated token; the candidate's markup differs. Neither matters to the contract.
    var token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    html.Append(CultureInfo.InvariantCulture, $"</table><p>Total: <span class=\"order-total\">{total.ToString("0.00", CultureInfo.InvariantCulture)}</span></p>");
    html.Append(isCandidate ? "<footer>Shop 2.0</footer>" : $"<input type=\"hidden\" name=\"token\" value=\"{token}\"/>");
    html.Append("</body></html>");
    return Results.Content(html.ToString(), "text/html; charset=utf-8");
});

app.MapPost("/checkout", async (HttpContext context) =>
{
    _ = await context.Request.ReadFromJsonAsync<CheckoutRequest>();
    var cart = CartOf(context);
    Line[] lines;
    decimal discount;
    lock (cart)
    {
        lines = [.. cart.Lines];
        discount = cart.Discount;
    }

    if (lines.Length == 0)
    {
        if (!isCandidate)
        {
            // The legacy defect: an empty cart reaches code that assumes a first line.
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await context.Response.WriteAsync("Object reference not set to an instance of an object.");
            return Results.Empty;
        }

        return Results.Json(new { error = "cart-empty" }, statusCode: 400);
    }

    var subtotal = lines.Sum(l => l.Product.Price * l.Quantity);
    var total = subtotal - (isCandidate ? discount : Math.Min(discount, subtotal));
    var orderId = Guid.NewGuid();
    lock (cart)
    {
        cart.Lines.Clear();
        cart.Discount = 0;
    }

    return Results.Created($"/orders/{orderId}", new { orderId, createdAt = DateTimeOffset.UtcNow, total });
});

app.MapPost("/__sk/reset", () =>
{
    if (!allowReset)
    {
        return Results.NotFound();
    }

    carts.Clear();
    return Results.NoContent();
});

app.MapGet("/health", () => Results.Text(variant));

app.Run();

/// <summary>The entry point, visible to test hosts.</summary>
public partial class Program;

internal sealed record Product(string Sku, string Name, decimal Price);

internal sealed record Line(Product Product, int Quantity);

internal sealed class Cart
{
    public List<Line> Lines { get; } = [];

    public decimal Discount { get; set; }
}

internal sealed record AddItem(string? Sku, int Quantity);

internal sealed record Coupon(string? Code);

internal sealed record CheckoutRequest(string? Email);

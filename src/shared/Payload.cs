using System.Security.Cryptography;
using System.Text.Json;

namespace CompressionDemo;

// One page of sales orders as an API would send it, about 1 MB of JSON.
// Built from a seeded Random and whole numbers only, so .NET 10 and .NET 11
// produce the same bytes (the probe prints the SHA-256 to show it).
public static class Payload
{
    public const int Seed = 68;
    public const int OrderCount = 1500;

    static readonly Lazy<byte[]> json = new(Build);

    public static byte[] Json => json.Value;

    public static string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Json));

    static readonly string[] FirstNames =
    [
        "Layla", "Omar", "Sara", "Youssef", "Amina", "Karim", "Nour", "Hassan", "Fatima", "Mehdi",
        "Claire", "Julien", "Emma", "Lucas", "Sofia", "Daniel", "Aisha", "Tariq", "Mariam", "Adam",
    ];

    static readonly string[] LastNames =
    [
        "Haddad", "Mansour", "Khalil", "Benali", "Gharbi", "Tremblay", "Gagnon", "Roy", "Martin",
        "Dubois", "Smith", "Johnson", "Al-Amin", "Saleh", "Nasser", "Trabelsi", "Lefebvre", "Chen",
    ];

    static readonly (string City, string Country, string Currency)[] Places =
    [
        ("Doha", "QA", "QAR"), ("Dubai", "AE", "AED"), ("Abu Dhabi", "AE", "AED"), ("Riyadh", "SA", "SAR"),
        ("Jeddah", "SA", "SAR"), ("Kuwait City", "KW", "KWD"), ("Manama", "BH", "BHD"), ("Muscat", "OM", "OMR"),
        ("Tunis", "TN", "TND"), ("Sfax", "TN", "TND"), ("Montreal", "CA", "CAD"), ("Quebec City", "CA", "CAD"),
        ("Toronto", "CA", "CAD"), ("Paris", "FR", "EUR"), ("Lyon", "FR", "EUR"),
    ];

    static readonly string[] Streets =
    [
        "Corniche", "Al Wahda", "King Fahd", "Habib Bourguiba", "Saint-Denis", "Sainte-Catherine",
        "Queen", "Rivoli", "Salwa", "Sheikh Zayed", "Olaya", "Gulf", "Peel", "Bay", "Victor Hugo",
    ];

    static readonly (string Sku, string Name, int PriceCents)[] Products =
    [
        ("KB-104", "Mechanical keyboard", 8990), ("MS-220", "Wireless mouse", 2990),
        ("MN-270", "27-inch monitor", 32900), ("DK-410", "USB-C dock", 14900),
        ("LS-015", "Laptop stand", 3950), ("HP-700", "Noise-cancelling headphones", 24900),
        ("WC-108", "Webcam 1080p", 6490), ("DL-033", "Desk lamp", 4200),
        ("CH-900", "Office chair", 27900), ("SD-140", "Standing desk", 54900),
        ("CB-002", "HDMI cable 2 m", 1290), ("SS-001", "Portable SSD 1 TB", 11900),
        ("PC-065", "Phone charger 65 W", 3490), ("NB-005", "Notebook A5, pack of 5", 1590),
        ("WM-012", "Whiteboard markers, 12 colours", 990),
    ];

    static readonly string[] Colours = ["BLK", "WHT", "GRY", "BLU"];
    static readonly string[] Statuses = ["Pending", "Paid", "Shipped", "Delivered", "Cancelled", "Refunded"];
    static readonly string[] Carriers = ["Aramex", "DHL Express", "Canada Post", "La Poste", "SMSA"];
    static readonly string[] Tags = ["gift", "priority", "b2b", "first-order", "repeat", "pickup"];
    static readonly string?[] Notes =
    [
        null, null, null, null, "Leave at reception", "Call before delivery", "Invoice to the finance team",
        "Deliver after 4 pm", null, "Fragile, please pack with care",
    ];

    public sealed record Customer(int Id, string Name, string Email, string Phone);
    public sealed record Address(string Line1, string City, string PostalCode, string Country);
    public sealed record Line(string Sku, string Name, int Quantity, decimal UnitPrice, decimal Amount);
    public sealed record Shipment(string Carrier, string TrackingNumber, DateTime? ShippedAt);
    public sealed record Order(
        int Id, string Number, DateTime CreatedAt, string Status, string Currency,
        Customer Customer, Address ShippingAddress, IReadOnlyList<Line> Lines,
        decimal Subtotal, decimal Tax, decimal Total, Shipment? Shipment,
        IReadOnlyList<string> Tags, string? Note);
    public sealed record OrderPage(int Page, int PageSize, int TotalCount, IReadOnlyList<Order> Items);

    // Cents to a decimal with exactly two places, without going through double.
    static decimal Money(long cents) => new((int)cents, (int)(cents >> 32), 0, false, 2);

    static byte[] Build()
    {
        var random = new Random(Seed);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var orders = new List<Order>(OrderCount);

        for (var i = 0; i < OrderCount; i++)
        {
            var id = 100_001 + i;
            var first = FirstNames[random.Next(FirstNames.Length)];
            var last = LastNames[random.Next(LastNames.Length)];
            var place = Places[random.Next(Places.Length)];
            var customerId = 4_000 + random.Next(1_500);
            var customer = new Customer(
                customerId,
                $"{first} {last}",
                $"{first.ToLowerInvariant()}.{last.ToLowerInvariant()}{customerId % 97}@example.com",
                $"+{random.Next(1, 999)} {random.Next(100, 999)} {random.Next(1000, 9999)}");
            var address = new Address(
                $"{random.Next(1, 400)} {Streets[random.Next(Streets.Length)]} Street",
                place.City,
                $"{random.Next(10000, 99999)}",
                place.Country);

            var lines = new List<Line>();
            var lineCount = 1 + random.Next(4);
            long subtotal = 0;
            for (var l = 0; l < lineCount; l++)
            {
                var product = Products[random.Next(Products.Length)];
                var quantity = 1 + random.Next(5);
                var unit = product.PriceCents + random.Next(-200, 200) * 5;
                subtotal += (long)unit * quantity;
                lines.Add(new Line(
                    $"{product.Sku}-{Colours[random.Next(Colours.Length)]}",
                    product.Name, quantity, Money(unit), Money((long)unit * quantity)));
            }

            var tax = subtotal * 5 / 100;
            var createdAt = start.AddMinutes(random.Next(0, 300 * 24 * 60));
            var status = Statuses[random.Next(Statuses.Length)];
            Shipment? shipment = status is "Shipped" or "Delivered"
                ? new Shipment(
                    Carriers[random.Next(Carriers.Length)],
                    $"TRK{random.Next(10_000_000, 99_999_999)}{random.Next(100, 999)}",
                    createdAt.AddHours(random.Next(4, 72)))
                : null;
            var tags = new List<string>();
            foreach (var tag in Tags)
            {
                if (random.Next(5) == 0) tags.Add(tag);
            }

            orders.Add(new Order(
                id, $"SO-2026-{id:D6}", createdAt, status, place.Currency, customer, address, lines,
                Money(subtotal), Money(tax), Money(subtotal + tax), shipment, tags,
                Notes[random.Next(Notes.Length)]));
        }

        var page = new OrderPage(1, OrderCount, OrderCount, orders);
        return JsonSerializer.SerializeToUtf8Bytes(page, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}

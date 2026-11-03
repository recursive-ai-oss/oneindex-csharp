namespace OneIndex.AotSmoke;

public class ShipTo
{
    public string City { get; set; } = string.Empty;

    public int Zip { get; set; }
}

public class Order
{
    public string Customer { get; set; } = string.Empty;

    public ShipTo? Ship { get; set; }

    public List<ShipTo> Others { get; set; } = [];

    public double? Total { get; set; }
}

public class OrderDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [Dynamic]
    public Order Order { get; set; } = new();
}

/// <summary>A <c>[Dynamic]</c> class of plain properties is written to and read from JSON by generated code, which must survive trimming and AOT.</summary>
public static class DynamicObjectScenario
{
    public static void Run()
    {
        using SearchIndex<OrderDoc> index = SearchIndex.InMemory<OrderDoc>();
        index.Upsert(new OrderDoc { Id = "o1", Order = new Order { Customer = "acme", Ship = new ShipTo { City = "Paris", Zip = 75001 }, Others = [new ShipTo { City = "Oslo" }], Total = 12.5 } });
        index.Commit();
        SearchHit<OrderDoc> hit = index.Search().Where("Order.Ship.City = Paris").Take(5).Single();
        if (hit.Document.Order.Customer != "acme" || hit.Document.Order.Ship?.Zip != 75001 || hit.Document.Order.Others[0].City != "Oslo" || hit.Document.Order.Total != 12.5)
        {
            throw new InvalidOperationException("dynamic object round trip failed");
        }

        Console.WriteLine("dynamic object: ok");
    }
}

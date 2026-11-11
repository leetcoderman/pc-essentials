namespace PCEssentials.Services.Agents;

public sealed partial class PriceList
{
    /// The list embedded in the app; an empty one when it cannot be read, which leaves every model unpriced.
    public static PriceList Load()
    {
        try
        {
            using var stream = typeof(PriceList).Assembly.GetManifestResourceStream("PCEssentials.Resources.agent-prices.json");
            return stream is null ? new PriceList() : Parse(stream);
        }
        catch (Exception ex)
        {
            Core.Log.Error("prices", ex);
            return new PriceList();
        }
    }
}

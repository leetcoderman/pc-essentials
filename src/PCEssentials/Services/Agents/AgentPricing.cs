using System.IO;
using System.Text.Json;

namespace PCEssentials.Services.Agents;

/// USD per million tokens for Claude models, from agent-prices.json (embedded, shared with the Mac app).
public sealed class ModelPrice
{
    public string Id { get; set; } = "";
    public double Input { get; set; }
    public double Output { get; set; }
    public double CacheRead { get; set; }
    public double? CacheWrite { get; set; }
    public double? CacheWriteLong { get; set; }
    public double? FastMultiplier { get; set; }
    public LongContextPrice? LongContext { get; set; }
}

public sealed class LongContextPrice
{
    public long Above { get; set; }
    public double InputMultiplier { get; set; } = 1;
    public double OutputMultiplier { get; set; } = 1;
}

public sealed class PlanInfo
{
    public string Match { get; set; } = "";
    public string Name { get; set; } = "";
    public double? Monthly { get; set; }
}

public sealed class ProviderPrices
{
    public double WebSearch { get; set; }
    public double UsOnlyMultiplier { get; set; } = 1;
    public List<ModelPrice> Models { get; set; } = [];
    public List<PlanInfo> Plans { get; set; } = [];
}

public sealed class PriceList
{
    public ProviderPrices Claude { get; set; } = new();

    public static PriceList Load()
    {
        try
        {
            using var stream = typeof(PriceList).Assembly.GetManifestResourceStream("PCEssentials.Resources.agent-prices.json");
            if (stream is null) return new PriceList();
            return JsonSerializer.Deserialize<PriceList>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PriceList();
        }
        catch (Exception ex)
        {
            Core.Log.Error("prices", ex);
            return new PriceList();
        }
    }

    /// Finds the price family for a model id: longest family id that is a prefix ending at a boundary.
    public ModelPrice? Match(string model)
    {
        var id = model.ToLowerInvariant();
        var claudeAt = id.IndexOf("claude-", StringComparison.Ordinal);
        if (claudeAt > 0) id = id[claudeAt..];
        var slash = id.LastIndexOf('/');
        if (slash >= 0) id = id[(slash + 1)..];
        var cut = id.IndexOfAny(['@', '[']);
        if (cut >= 0) id = id[..cut];
        if (id.StartsWith("claude-")) id = id.Replace('.', '-');

        ModelPrice? best = null;
        foreach (var m in Claude.Models)
        {
            var family = m.Id.ToLowerInvariant();
            if (!id.StartsWith(family)) continue;
            var rest = id[family.Length..];
            if (rest.Length > 0)
            {
                if (rest[0] is not ('-' or '_' or ':')) continue;
                var tail = rest[1..];
                var digits = tail.TakeWhile(char.IsDigit).Count();
                if (digits is > 0 and < 4) continue;
            }
            if (best is null || family.Length > best.Id.Length) best = m;
        }
        return best;
    }

    public double? Cost(AgentRecord r)
    {
        var price = Match(r.Model);
        if (price is null) return null;
        var prov = Claude;
        var mult = (r.Fast ? price.FastMultiplier ?? 1 : 1) * (r.UsOnly ? prov.UsOnlyMultiplier : 1);
        double inR = mult, outR = mult;
        if (price.LongContext is { } lc && r.Input + r.CacheWrite + r.CacheRead > lc.Above)
        {
            inR *= lc.InputMultiplier;
            outR *= lc.OutputMultiplier;
        }
        var cacheWrite = price.CacheWrite ?? price.Input;
        var cacheWriteLong = price.CacheWriteLong ?? cacheWrite;
        var longPart = Math.Min(r.CacheWriteLong, r.CacheWrite);
        return (r.Input * price.Input + (r.CacheWrite - longPart) * cacheWrite + longPart * cacheWriteLong + r.CacheRead * price.CacheRead) * inR / 1e6
             + r.Output * price.Output * outR / 1e6
             + r.WebSearches * prov.WebSearch;
    }

    public PlanInfo? Plan(string? tier)
    {
        if (string.IsNullOrEmpty(tier)) return null;
        var t = tier.ToLowerInvariant();
        return Claude.Plans.FirstOrDefault(p => t.Contains(p.Match.ToLowerInvariant()));
    }
}

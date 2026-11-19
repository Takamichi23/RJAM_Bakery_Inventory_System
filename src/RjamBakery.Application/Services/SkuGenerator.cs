using Microsoft.EntityFrameworkCore;

namespace RjamBakery.Application.Services;

/// <summary>
/// Generates sequential unique SKUs cleanly when callers omit a custom SKU.
/// </summary>
internal static class SkuGenerator
{
    public static async Task<string> ResolveSkuAsync(
        string? requestedSku,
        string? existingEntitySku,
        IQueryable<string> allExistingSkus,
        string prefix,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(requestedSku))
        {
            return requestedSku.Trim().ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(existingEntitySku))
        {
            return existingEntitySku;
        }

        var normalizedPrefix = $"{prefix.ToUpperInvariant()}-";
        var matchingSkus = await allExistingSkus
            .Where(s => s.StartsWith(normalizedPrefix))
            .ToListAsync(ct);

        var maxSequence = 0;
        foreach (var sku in matchingSkus)
        {
            var suffix = sku[normalizedPrefix.Length..];
            if (int.TryParse(suffix, out var parsed) && parsed > maxSequence)
            {
                maxSequence = parsed;
            }
        }

        return $"{normalizedPrefix}{(maxSequence + 1):D4}";
    }
}

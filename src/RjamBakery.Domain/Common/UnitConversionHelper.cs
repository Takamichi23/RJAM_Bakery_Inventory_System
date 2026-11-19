using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Domain.Common;

/// <summary>
/// Enforces dimensional unit conversions (Mass, Volume, Count) and configurable material pack-to-base conversions.
/// </summary>
public static class UnitConversionHelper
{
    public static bool AreCompatible(UnitOfMeasure source, UnitOfMeasure target, decimal? packToBaseFactor = null)
    {
        if (source.Dimension == target.Dimension)
        {
            return true;
        }

        return packToBaseFactor is > 0 && (IsPack(source) || IsPack(target));
    }

    /// <summary>
    /// Converts a quantity from <paramref name="sourceUnit"/> to <paramref name="targetUnit"/>.
    /// Supports configurable pack-to-stock-unit ratio when <paramref name="packToBaseFactor"/> is provided.
    /// </summary>
    public static decimal Convert(
        decimal quantity,
        UnitOfMeasure sourceUnit,
        UnitOfMeasure targetUnit,
        decimal? packToBaseFactor = null)
    {
        if (sourceUnit.Id == targetUnit.Id || string.Equals(sourceUnit.Code, targetUnit.Code, StringComparison.OrdinalIgnoreCase))
        {
            return quantity;
        }

        if (!AreCompatible(sourceUnit, targetUnit, packToBaseFactor))
        {
            throw new DomainValidationException(
                $"Cannot convert incompatible units from '{sourceUnit.Code}' ({sourceUnit.Dimension}) to '{targetUnit.Code}' ({targetUnit.Dimension}).");
        }

        if (IsPack(sourceUnit) && !IsPack(targetUnit))
        {
            var ratio = ResolvePackFactor(sourceUnit, targetUnit, packToBaseFactor);
            return decimal.Round(quantity * ratio, 4, MidpointRounding.AwayFromZero);
        }

        if (IsPack(targetUnit) && !IsPack(sourceUnit))
        {
            var ratio = ResolvePackFactor(targetUnit, sourceUnit, packToBaseFactor);
            return decimal.Round(quantity / ratio, 4, MidpointRounding.AwayFromZero);
        }

        if (sourceUnit.Dimension != targetUnit.Dimension)
        {
            throw new DomainValidationException(
                $"Cannot convert incompatible units from '{sourceUnit.Code}' ({sourceUnit.Dimension}) to '{targetUnit.Code}' ({targetUnit.Dimension}).");
        }

        if (sourceUnit.FactorToBase <= 0 || targetUnit.FactorToBase <= 0)
        {
            throw new DomainValidationException($"Invalid unit conversion factor between '{sourceUnit.Code}' and '{targetUnit.Code}'.");
        }

        decimal inBaseUnits = quantity * sourceUnit.FactorToBase;
        return decimal.Round(inBaseUnits / targetUnit.FactorToBase, 4, MidpointRounding.AwayFromZero);
    }

    private static bool IsPack(UnitOfMeasure unit)
        => string.Equals(unit.Code, "pack", StringComparison.OrdinalIgnoreCase);

    private static decimal ResolvePackFactor(UnitOfMeasure packUnit, UnitOfMeasure otherUnit, decimal? packToBaseFactor)
    {
        if (packToBaseFactor is > 0)
        {
            return packToBaseFactor.Value;
        }

        if (packUnit.Dimension == otherUnit.Dimension && packUnit.FactorToBase > 1)
        {
            return packUnit.FactorToBase;
        }

        throw new DomainValidationException(
            $"Converting between '{packUnit.Code}' and '{otherUnit.Code}' requires a valid Pack-to-Base conversion ratio greater than zero.");
    }
}

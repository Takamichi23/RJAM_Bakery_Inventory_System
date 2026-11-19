using FluentAssertions;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.UnitTests;

public sealed class UnitConversionTests
{
    private static readonly UnitOfMeasure Gram = new() { Id = 1, Code = "g", Name = "Gram", Dimension = UnitDimension.Mass, FactorToBase = 1m, IsBaseUnit = true };
    private static readonly UnitOfMeasure Kilogram = new() { Id = 2, Code = "kg", Name = "Kilogram", Dimension = UnitDimension.Mass, FactorToBase = 1000m };
    private static readonly UnitOfMeasure Milliliter = new() { Id = 3, Code = "mL", Name = "Milliliter", Dimension = UnitDimension.Volume, FactorToBase = 1m, IsBaseUnit = true };
    private static readonly UnitOfMeasure Liter = new() { Id = 4, Code = "L", Name = "Liter", Dimension = UnitDimension.Volume, FactorToBase = 1000m };
    private static readonly UnitOfMeasure Piece = new() { Id = 5, Code = "pcs", Name = "Pieces", Dimension = UnitDimension.Count, FactorToBase = 1m, IsBaseUnit = true };
    private static readonly UnitOfMeasure Pack = new() { Id = 6, Code = "pack", Name = "Pack", Dimension = UnitDimension.Count, FactorToBase = 1m };

    [Fact]
    public void Convert_GramsToKilograms_ReturnsExactDecimal()
    {
        var result = UnitConversionHelper.Convert(2500m, Gram, Kilogram);
        result.Should().Be(2.5000m);
    }

    [Fact]
    public void Convert_KilogramsToGrams_ReturnsExactDecimal()
    {
        var result = UnitConversionHelper.Convert(1.375m, Kilogram, Gram);
        result.Should().Be(1375.0000m);
    }

    [Fact]
    public void Convert_MillilitersToLiters_ReturnsExactDecimal()
    {
        var result = UnitConversionHelper.Convert(750m, Milliliter, Liter);
        result.Should().Be(0.7500m);
    }

    [Fact]
    public void Convert_PackToPieces_UsesConfigurablePackRatio()
    {
        var result = UnitConversionHelper.Convert(2.5m, Pack, Piece, packToBaseFactor: 30m);
        result.Should().Be(75m);
    }

    [Fact]
    public void Convert_PackToKilograms_UsesConfigurablePackRatio()
    {
        var result = UnitConversionHelper.Convert(2m, Pack, Kilogram, packToBaseFactor: 25m);
        result.Should().Be(50.0000m);
    }

    [Fact]
    public void Convert_PackWithoutRatio_ThrowsDomainValidationException()
    {
        var act = () => UnitConversionHelper.Convert(2m, Pack, Piece);
        act.Should().Throw<DomainValidationException>()
            .WithMessage("*Pack-to-Base*");
    }

    [Theory]
    [MemberData(nameof(IncompatiblePairs))]
    public void Convert_IncompatibleDimensions_ThrowsDomainValidationException(UnitOfMeasure source, UnitOfMeasure target)
    {
        var act = () => UnitConversionHelper.Convert(10m, source, target);
        act.Should().Throw<DomainValidationException>()
            .WithMessage("*incompatible units*");
    }

    public static IEnumerable<object[]> IncompatiblePairs()
    {
        yield return [Kilogram, Liter];
        yield return [Gram, Piece];
        yield return [Milliliter, Pack];
    }
}

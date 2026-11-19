using FluentAssertions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;

namespace RjamBakery.UnitTests;

public sealed class ValidatorTests
{
    [Fact]
    public void SaveProductRequestValidator_RejectsNegativePriceOrThreshold()
    {
        var validator = new SaveProductRequestValidator();
        var result = validator.Validate(new SaveProductRequest(
            null, 1, "PND-01", "Pandesal", "Fresh bread", 5, -5.00m, -1m, null, null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SaveProductRequest.SellingPrice));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SaveProductRequest.MinStockLevel));
    }

    [Fact]
    public void RawMaterialAdjustmentRequestValidator_RequiresNonZeroDeltaAndReason()
    {
        var validator = new RawMaterialAdjustmentRequestValidator();
        var result = validator.Validate(new RawMaterialAdjustmentRequest(1, 0m, 2, ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(RawMaterialAdjustmentRequest.QuantityDelta));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(RawMaterialAdjustmentRequest.Reason));
    }

    [Fact]
    public void CreateUserRequestValidator_RejectsUnknownRole()
    {
        var validator = new CreateUserRequestValidator();
        var result = validator.Validate(new CreateUserRequest(
            "staff1", "Staff One", "staff1@rjam.local", "SuperAdmin", "Pass1234"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateUserRequest.Role));
    }

    [Fact]
    public void SaveCatalogValidators_AllowBlankSkuForAutomaticGeneration()
    {
        var prodResult = new SaveProductRequestValidator().Validate(
            new SaveProductRequest(null, 1, null, "Spanish Bread", "Sweet roll", 1, 10m, 5m, null, null));
        prodResult.IsValid.Should().BeTrue();

        var matResult = new SaveRawMaterialRequestValidator().Validate(
            new SaveRawMaterialRequest(null, "", "Bread Flour", "", 1, null, 5m, null));
        matResult.IsValid.Should().BeTrue();
    }
}

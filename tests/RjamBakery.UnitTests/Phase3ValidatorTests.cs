using FluentAssertions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Enums;

namespace RjamBakery.UnitTests;

public sealed class Phase3ValidatorTests
{
    [Fact]
    public void SaveSupplierRequestValidator_RequiresNameAndValidEmail()
    {
        var validator = new SaveSupplierRequestValidator();
        var result = validator.Validate(new SaveSupplierRequest(
            null, "", "Juan", "0917", "not-an-email", "Manila", SupplierStatus.Active, ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SaveSupplierRequest.Name));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SaveSupplierRequest.Email));
    }

    [Fact]
    public void CreateMaterialReceiptRequestValidator_RejectsEmptyItemsOrZeroQuantity()
    {
        var validator = new CreateMaterialReceiptRequestValidator();
        var emptyResult = validator.Validate(new CreateMaterialReceiptRequest(1, DateTime.UtcNow, "", []));
        emptyResult.IsValid.Should().BeFalse();

        var badLineResult = validator.Validate(new CreateMaterialReceiptRequest(
            1, DateTime.UtcNow, "", [new MaterialReceiptItemInput(1, 0m, 2, -1m)]));
        badLineResult.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SaveRecipeRequestValidator_RequiresYieldAndIngredients()
    {
        var validator = new SaveRecipeRequestValidator();
        var result = validator.Validate(new SaveRecipeRequest(1, 0m, 5, "", []));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SaveRecipeRequest.BatchYieldQuantity));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SaveRecipeRequest.Ingredients));
    }

    [Fact]
    public void FinalizeProductionRequestValidator_RejectsOversizedIdempotencyKey()
    {
        var validator = new FinalizeProductionRequestValidator();
        var result = validator.Validate(new FinalizeProductionRequest(
            null, 1, 100m, 100m, new DateOnly(2026, 11, 16), null, "", [], true, new string('X', 41)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(FinalizeProductionRequest.IdempotencyKey));
    }
}

using FluentAssertions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Enums;

namespace RjamBakery.UnitTests;

public sealed class Phase4ValidatorTests
{
    [Fact]
    public void CreateDeliveryRequestValidator_RequiresItemsAndPositiveQty()
    {
        var validator = new CreateDeliveryRequestValidator();
        validator.Validate(new CreateDeliveryRequest("Driver", "", [])).IsValid.Should().BeFalse();
        validator.Validate(new CreateDeliveryRequest("Driver", "", [new DeliveryItemInput(1, 0m)])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void ConfirmDeliveryReceivingRequestValidator_RejectsNegativeReceivedQty()
    {
        var validator = new ConfirmDeliveryReceivingRequestValidator();
        var result = validator.Validate(new ConfirmDeliveryReceivingRequest(null, [new ReceiveDeliveryItemInput(1, -2m, "Bad")]));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ReconcileDeliveryRequestValidator_RequiresRemarks()
    {
        var validator = new ReconcileDeliveryRequestValidator();
        var result = validator.Validate(new ReconcileDeliveryRequest(
            [new ReconcileDeliveryItemInput(1, DiscrepancyResolutionType.TransitLossWriteOff, "")]));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateStockAdjustmentRequestValidator_EnforcesLocationTargetId()
    {
        var validator = new CreateStockAdjustmentRequestValidator();
        var storeMissingProduct = validator.Validate(new CreateStockAdjustmentRequest(
            AdjustmentLocation.Store, null, null, null, 5m, "Count correction"));
        storeMissingProduct.IsValid.Should().BeFalse();

        var zeroDelta = validator.Validate(new CreateStockAdjustmentRequest(
            AdjustmentLocation.Store, 1, null, null, 0m, "Zero delta"));
        zeroDelta.IsValid.Should().BeFalse();
    }
}

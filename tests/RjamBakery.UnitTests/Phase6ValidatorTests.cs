using FluentAssertions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Enums;

namespace RjamBakery.UnitTests;

public sealed class Phase6ValidatorTests
{
    [Fact]
    public void RecordWasteOrReturnRequestValidator_RequiresLocationTargetAndPositiveQty()
    {
        var validator = new RecordWasteOrReturnRequestValidator();

        validator.Validate(new RecordWasteOrReturnRequest(
            InventoryLocation.Store, null, null, null, null, WasteReturnCategory.Damaged, 0m, "")).IsValid.Should().BeFalse();

        validator.Validate(new RecordWasteOrReturnRequest(
            InventoryLocation.Factory, null, null, null, null, WasteReturnCategory.Spoiled, 5m, "Spoiled batch")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void InspectCustomerReturnRequestValidator_RejectsPendingOutcomeOrEmptyNotes()
    {
        var validator = new InspectCustomerReturnRequestValidator();
        validator.Validate(new InspectCustomerReturnRequest(InspectionOutcome.PendingInspection, "Note")).IsValid.Should().BeFalse();
        validator.Validate(new InspectCustomerReturnRequest(InspectionOutcome.RestockedToSellable, "")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void SubmitDailyClosingRequestValidator_RejectsEmptyItemsOrNegativeCount()
    {
        var validator = new SubmitDailyClosingRequestValidator();
        validator.Validate(new SubmitDailyClosingRequest(new DateOnly(2026, 11, 16), "", [])).IsValid.Should().BeFalse();
        validator.Validate(new SubmitDailyClosingRequest(new DateOnly(2026, 11, 16), "",
            [new SubmitDailyClosingItemInput(1, -1m)])).IsValid.Should().BeFalse();
    }
}

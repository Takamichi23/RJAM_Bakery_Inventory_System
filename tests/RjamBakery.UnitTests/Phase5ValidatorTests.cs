using FluentAssertions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Enums;

namespace RjamBakery.UnitTests;

public sealed class Phase5ValidatorTests
{
    [Fact]
    public void CompletePosSaleRequestValidator_RejectsEmptyCartDuplicateProductsAndNegativeAmounts()
    {
        var validator = new CompletePosSaleRequestValidator();

        validator.Validate(new CompletePosSaleRequest("", PaymentMethod.Cash, 0m, 0m, [])).IsValid.Should().BeFalse();

        var dupLine = validator.Validate(new CompletePosSaleRequest(
            "idem-1",
            PaymentMethod.Cash,
            0m,
            50m,
            [new PosSaleItemInput(1, 1m), new PosSaleItemInput(1, 2m)]));
        dupLine.IsValid.Should().BeFalse();

        var negDiscount = validator.Validate(new CompletePosSaleRequest(
            "idem-2",
            PaymentMethod.Cash,
            -5m,
            50m,
            [new PosSaleItemInput(1, 1m)]));
        negDiscount.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ReversePosTransactionRequestValidator_RejectsCompletedStatusAndEmptyReason()
    {
        var validator = new ReversePosTransactionRequestValidator();
        var result = validator.Validate(new ReversePosTransactionRequest(PosTransactionStatus.Completed, ""));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReversePosTransactionRequest.TargetStatus));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReversePosTransactionRequest.Reason));
    }
}

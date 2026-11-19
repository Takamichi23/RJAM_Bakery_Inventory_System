using FluentValidation;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Validators;

public sealed class CreateDeliveryRequestValidator : AbstractValidator<CreateDeliveryRequest>
{
    public CreateDeliveryRequestValidator()
    {
        RuleFor(x => x.DriverOrCourierName).MaximumLength(120);
        RuleFor(x => x.Notes).MaximumLength(500);
        RuleFor(x => x.Items).NotEmpty().WithMessage("At least one product line is required for a delivery.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).GreaterThan(0);
            item.RuleFor(i => i.RequestedQuantity).GreaterThan(0).WithMessage("Requested quantity must be greater than zero.");
        });
    }
}

public sealed class ConfirmDeliveryReceivingRequestValidator : AbstractValidator<ConfirmDeliveryReceivingRequest>
{
    public ConfirmDeliveryReceivingRequestValidator()
    {
        RuleFor(x => x.Notes).MaximumLength(500);
        RuleFor(x => x.Items).NotEmpty().WithMessage("Delivery receiving confirmation requires item lines.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.DeliveryItemId).GreaterThan(0);
            item.RuleFor(i => i.ReceivedQuantity).GreaterThanOrEqualTo(0).WithMessage("Received quantity cannot be negative.");
            item.RuleFor(i => i.DiscrepancyReason).MaximumLength(300);
        });
    }
}

public sealed class ReconcileDeliveryRequestValidator : AbstractValidator<ReconcileDeliveryRequest>
{
    public ReconcileDeliveryRequestValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.DeliveryItemId).GreaterThan(0);
            item.RuleFor(i => i.ResolutionRemarks)
                .NotEmpty().WithMessage("Resolution remarks are required when reconciling a delivery discrepancy.")
                .MaximumLength(300);
        });
    }
}

public sealed class CreateStockAdjustmentRequestValidator : AbstractValidator<CreateStockAdjustmentRequest>
{
    public CreateStockAdjustmentRequestValidator()
    {
        RuleFor(x => x.QuantityDelta).NotEqual(0).WithMessage("Adjustment delta cannot be zero.");
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(300);
        RuleFor(x => x.ProductId).NotNull().When(x => x.Location == AdjustmentLocation.Store)
            .WithMessage("ProductId is required for Store stock adjustments.");
        RuleFor(x => x.RawMaterialId).NotNull().When(x => x.Location == AdjustmentLocation.RawMaterial)
            .WithMessage("RawMaterialId is required for Raw Material adjustments.");
        RuleFor(x => x.FactoryStockLotId).NotNull().When(x => x.Location == AdjustmentLocation.Factory)
            .WithMessage("FactoryStockLotId is required for Factory lot adjustments.");
    }
}

public sealed class DirectStoreAdjustmentRequestValidator : AbstractValidator<DirectStoreAdjustmentRequest>
{
    public DirectStoreAdjustmentRequestValidator()
    {
        RuleFor(x => x.ProductId).GreaterThan(0);
        RuleFor(x => x.QuantityDelta).NotEqual(0).WithMessage("Adjustment quantity delta cannot be zero.");
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(300);
    }
}

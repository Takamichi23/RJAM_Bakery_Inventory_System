using FluentValidation;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Validators;

public sealed class RecordWasteOrReturnRequestValidator : AbstractValidator<RecordWasteOrReturnRequest>
{
    public RecordWasteOrReturnRequestValidator()
    {
        RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("Quantity must be greater than zero.");
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(300);

        RuleFor(x => x.ProductId)
            .NotNull().When(x => x.Location == InventoryLocation.Store)
            .WithMessage("ProductId is required for Store waste/return records.");

        RuleFor(x => x)
            .Must(x => x.FactoryStockLotId.HasValue || x.RawMaterialId.HasValue)
            .When(x => x.Location == InventoryLocation.Factory)
            .WithMessage("Either FactoryStockLotId or RawMaterialId is required for Factory losses.");

        RuleFor(x => x.Location)
            .Must(l => l is InventoryLocation.Store or InventoryLocation.Factory)
            .WithMessage("Waste or return location must be Store or Factory.");
    }
}

public sealed class InspectCustomerReturnRequestValidator : AbstractValidator<InspectCustomerReturnRequest>
{
    public InspectCustomerReturnRequestValidator()
    {
        RuleFor(x => x.Outcome)
            .Must(o => o is InspectionOutcome.RestockedToSellable or InspectionOutcome.DisposedAsLoss)
            .WithMessage("Inspection outcome must be RestockedToSellable or DisposedAsLoss.");
        RuleFor(x => x.InspectionNotes).NotEmpty().MinimumLength(3).MaximumLength(300);
    }
}

public sealed class SubmitDailyClosingRequestValidator : AbstractValidator<SubmitDailyClosingRequest>
{
    public SubmitDailyClosingRequestValidator()
    {
        RuleFor(x => x.Notes).MaximumLength(500);
        RuleFor(x => x.Items).NotEmpty().WithMessage("At least one product count line is required.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).GreaterThan(0);
            item.RuleFor(i => i.ActualClosingStock).GreaterThanOrEqualTo(0).WithMessage("Physical count cannot be negative.");
            item.RuleFor(i => i.VarianceReason).MaximumLength(300);
        });
    }
}

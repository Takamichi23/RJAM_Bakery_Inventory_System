using FluentValidation;
using RjamBakery.Application.Contracts;

namespace RjamBakery.Application.Validators;

public sealed class SaveSupplierRequestValidator : AbstractValidator<SaveSupplierRequest>
{
    public SaveSupplierRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Supplier name is required.").MaximumLength(150);
        RuleFor(x => x.ContactPerson).MaximumLength(120);
        RuleFor(x => x.Phone).MaximumLength(40);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email)).MaximumLength(150);
        RuleFor(x => x.Address).MaximumLength(300);
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

public sealed class CreateMaterialReceiptRequestValidator : AbstractValidator<CreateMaterialReceiptRequest>
{
    public CreateMaterialReceiptRequestValidator()
    {
        RuleFor(x => x.SupplierId).GreaterThan(0).WithMessage("Supplier is required.");
        RuleFor(x => x.Remarks).MaximumLength(500);
        RuleFor(x => x.Items).NotEmpty().WithMessage("At least one received raw material line is required.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.RawMaterialId).GreaterThan(0);
            item.RuleFor(i => i.UnitId).GreaterThan(0);
            item.RuleFor(i => i.Quantity).GreaterThan(0).WithMessage("Received quantity must be greater than zero.");
            item.RuleFor(i => i.UnitCost).GreaterThanOrEqualTo(0).When(i => i.UnitCost.HasValue);
        });
    }
}

public sealed class SaveRecipeRequestValidator : AbstractValidator<SaveRecipeRequest>
{
    public SaveRecipeRequestValidator()
    {
        RuleFor(x => x.ProductId).GreaterThan(0).WithMessage("Product is required.");
        RuleFor(x => x.BatchYieldQuantity).GreaterThan(0).WithMessage("Batch yield quantity must be greater than zero.");
        RuleFor(x => x.YieldUnitId).GreaterThan(0).WithMessage("Yield unit is required.");
        RuleFor(x => x.Notes).MaximumLength(500);
        RuleFor(x => x.Ingredients).NotEmpty().WithMessage("A recipe must include at least one ingredient.");
        RuleForEach(x => x.Ingredients).ChildRules(ing =>
        {
            ing.RuleFor(i => i.RawMaterialId).GreaterThan(0);
            ing.RuleFor(i => i.UnitId).GreaterThan(0);
            ing.RuleFor(i => i.QuantityRequired).GreaterThan(0).WithMessage("Ingredient quantity must be greater than zero.");
        });
    }
}

public sealed class CreateProductionPlanRequestValidator : AbstractValidator<CreateProductionPlanRequest>
{
    public CreateProductionPlanRequestValidator()
    {
        RuleFor(x => x.ProductId).GreaterThan(0);
        RuleFor(x => x.PlannedQuantity).GreaterThan(0).WithMessage("Planned quantity must be greater than zero.");
        RuleFor(x => x.Remarks).MaximumLength(500);
    }
}

public sealed class FinalizeProductionRequestValidator : AbstractValidator<FinalizeProductionRequest>
{
    public FinalizeProductionRequestValidator()
    {
        RuleFor(x => x.ProductId).GreaterThan(0);
        RuleFor(x => x.PlannedQuantity).GreaterThan(0);
        RuleFor(x => x.ActualOutputQuantity).GreaterThan(0).WithMessage("Actual produced output quantity must be greater than zero.");
        RuleFor(x => x.Remarks).MaximumLength(500);
        RuleFor(x => x.IdempotencyKey).MaximumLength(40).When(x => !string.IsNullOrWhiteSpace(x.IdempotencyKey));
        RuleForEach(x => x.Consumptions).ChildRules(c =>
        {
            c.RuleFor(x => x.RawMaterialId).GreaterThan(0);
            c.RuleFor(x => x.UnitId).GreaterThan(0);
            c.RuleFor(x => x.ActualConsumedQuantity).GreaterThan(0).WithMessage("Consumed ingredient quantity must be greater than zero.");
        });
    }
}

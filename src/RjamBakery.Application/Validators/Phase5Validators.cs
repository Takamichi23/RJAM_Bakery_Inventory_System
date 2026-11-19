using FluentValidation;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Validators;

public sealed class CompletePosSaleRequestValidator : AbstractValidator<CompletePosSaleRequest>
{
    public CompletePosSaleRequestValidator()
    {
        RuleFor(x => x.ClientIdempotencyKey)
            .NotEmpty().WithMessage("A client idempotency key is required to prevent duplicate transactions.")
            .MaximumLength(80);

        RuleFor(x => x.DiscountAmount)
            .GreaterThanOrEqualTo(0).WithMessage("Discount amount cannot be negative.");

        RuleFor(x => x.AmountTendered)
            .GreaterThanOrEqualTo(0).WithMessage("Amount tendered cannot be negative.");

        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("Cart cannot be empty.")
            .Must(items => items.Select(i => i.ProductId).Distinct().Count() == items.Count)
            .WithMessage("Duplicate product lines in cart must be combined into a single line.");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).GreaterThan(0);
            item.RuleFor(i => i.Quantity).GreaterThan(0).WithMessage("Item quantity must be greater than zero.");
        });
    }
}

public sealed class ReversePosTransactionRequestValidator : AbstractValidator<ReversePosTransactionRequest>
{
    public ReversePosTransactionRequestValidator()
    {
        RuleFor(x => x.TargetStatus)
            .Must(s => s is PosTransactionStatus.Voided or PosTransactionStatus.Refunded)
            .WithMessage("Target status must be Voided or Refunded.");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("A mandatory reason is required for voids and refunds.")
            .MinimumLength(3)
            .MaximumLength(300);
    }
}

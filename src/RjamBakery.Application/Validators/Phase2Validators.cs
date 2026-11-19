using FluentValidation;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Common;

namespace RjamBakery.Application.Validators;

public static class ValidationHelper
{
    public static async Task ValidateAndThrowAsync<T>(
        IValidator<T> validator,
        T instance,
        CancellationToken ct = default)
    {
        var result = await validator.ValidateAsync(instance, ct);
        if (!result.IsValid)
        {
            var message = string.Join("; ", result.Errors.Select(e => e.ErrorMessage).Distinct());
            throw new DomainValidationException(message);
        }
    }
}

public sealed class SaveCategoryRequestValidator : AbstractValidator<SaveCategoryRequest>
{
    public SaveCategoryRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Category name is required.")
            .MaximumLength(100);

        RuleFor(x => x.Description)
            .MaximumLength(300);

        RuleFor(x => x.DisplayOrder)
            .GreaterThanOrEqualTo(0);
    }
}

public sealed class SaveProductRequestValidator : AbstractValidator<SaveProductRequest>
{
    public SaveProductRequestValidator()
    {
        RuleFor(x => x.CategoryId)
            .GreaterThan(0).WithMessage("A valid product category is required.");

        RuleFor(x => x.Sku)
            .MaximumLength(50).When(x => !string.IsNullOrWhiteSpace(x.Sku));

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Product name is required.")
            .MaximumLength(150);

        RuleFor(x => x.Description)
            .MaximumLength(500);

        RuleFor(x => x.BaseUnitId)
            .GreaterThan(0).WithMessage("Base unit of measure is required.");

        RuleFor(x => x.SellingPrice)
            .GreaterThanOrEqualTo(0).WithMessage("Selling price cannot be negative.");

        RuleFor(x => x.MinStockLevel)
            .GreaterThanOrEqualTo(0).WithMessage("Minimum stock level cannot be negative.");

        RuleFor(x => x.ShelfLifeDays)
            .GreaterThan(0).When(x => x.ShelfLifeDays.HasValue)
            .WithMessage("Shelf life days must be positive when provided.");
    }
}

public sealed class SaveRawMaterialRequestValidator : AbstractValidator<SaveRawMaterialRequest>
{
    public SaveRawMaterialRequestValidator()
    {
        RuleFor(x => x.Sku)
            .MaximumLength(50).When(x => !string.IsNullOrWhiteSpace(x.Sku));

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Raw material name is required.")
            .MaximumLength(150);

        RuleFor(x => x.Description)
            .MaximumLength(500);

        RuleFor(x => x.UnitId)
            .GreaterThan(0).WithMessage("Unit of measure is required.");

        RuleFor(x => x.PackToBaseRatio)
            .GreaterThan(0).When(x => x.PackToBaseRatio.HasValue)
            .WithMessage("Pack-to-base ratio must be greater than zero.");

        RuleFor(x => x.MinStockLevel)
            .GreaterThanOrEqualTo(0).WithMessage("Minimum stock level cannot be negative.");
    }
}

public sealed class RawMaterialAdjustmentRequestValidator : AbstractValidator<RawMaterialAdjustmentRequest>
{
    public RawMaterialAdjustmentRequestValidator()
    {
        RuleFor(x => x.RawMaterialId)
            .GreaterThan(0).WithMessage("Raw material is required.");

        RuleFor(x => x.UnitId)
            .GreaterThan(0).WithMessage("Unit of measure is required.");

        RuleFor(x => x.QuantityDelta)
            .NotEqual(0).WithMessage("Adjustment quantity delta cannot be zero.");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("A mandatory adjustment reason is required.")
            .MinimumLength(3).WithMessage("Adjustment reason must be at least 3 characters.")
            .MaximumLength(300);
    }
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.UserName)
            .NotEmpty().MinimumLength(3).MaximumLength(50);

        RuleFor(x => x.FullName)
            .NotEmpty().MaximumLength(120);

        RuleFor(x => x.Email)
            .NotEmpty().EmailAddress().MaximumLength(150);

        RuleFor(x => x.Role)
            .Must(r => AppRoles.All.Contains(r))
            .WithMessage("Role must be one of Owner, Manager, Baker, or Cashier.");

        RuleFor(x => x.TemporaryPassword)
            .NotEmpty().MinimumLength(8).WithMessage("Password must be at least 8 characters.");
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(150);
        RuleFor(x => x.Role)
            .Must(r => AppRoles.All.Contains(r))
            .WithMessage("Role must be one of Owner, Manager, Baker, or Cashier.");
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.NewTemporaryPassword)
            .NotEmpty().MinimumLength(8).WithMessage("Password must be at least 8 characters.");
    }
}

public sealed class UpdateSystemSettingRequestValidator : AbstractValidator<UpdateSystemSettingRequest>
{
    public UpdateSystemSettingRequestValidator()
    {
        RuleFor(x => x.SettingKey).NotEmpty().MaximumLength(100);
        RuleFor(x => x.SettingValue).NotNull().MaximumLength(500);
    }
}

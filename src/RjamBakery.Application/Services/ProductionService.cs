using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IProductionService
{
    Task<PagedResult<ProductionBatchDto>> GetPagedAsync(ProductionQuery query, CancellationToken ct = default);
    Task<ProductionBatchDto> GetByIdAsync(long id, CancellationToken ct = default);
    Task<ProductionBatchDto> CreatePlanAsync(CreateProductionPlanRequest request, CancellationToken ct = default);
    Task<ProductionBatchDto> FinalizeProductionAsync(FinalizeProductionRequest request, CancellationToken ct = default);
    Task CancelPlanAsync(long batchId, string reason, CancellationToken ct = default);
}

public sealed class ProductionService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IInventoryLedgerService ledger,
    IAuditLogService audit,
    ISystemSettingService settingService,
    IDateTimeProvider clock,
    IValidator<CreateProductionPlanRequest> planValidator,
    IValidator<FinalizeProductionRequest> finalizeValidator) : IProductionService
{
    public async Task<PagedResult<ProductionBatchDto>> GetPagedAsync(ProductionQuery query, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        var page = Math.Max(1, query.PageNumber);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var q = db.ProductionBatches.AsNoTracking();

        if (query.ProductId.HasValue)
        {
            q = q.Where(x => x.ProductId == query.ProductId.Value);
        }

        if (query.Status.HasValue)
        {
            q = q.Where(x => x.Status == query.Status.Value);
        }

        if (query.FromDate.HasValue)
        {
            q = q.Where(x => x.ProductionDate >= query.FromDate.Value);
        }

        if (query.ToDate.HasValue)
        {
            q = q.Where(x => x.ProductionDate <= query.ToDate.Value);
        }

        var total = await q.CountAsync(ct);

        var items = await ProjectBatches(
            q.OrderByDescending(x => x.ProductionDate).ThenByDescending(x => x.Id)
                .Skip((page - 1) * size)
                .Take(size))
            .ToListAsync(ct);

        return new PagedResult<ProductionBatchDto>(items, total, page, size);
    }

    public async Task<ProductionBatchDto> GetByIdAsync(long id, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        return await ProjectBatches(db.ProductionBatches.AsNoTracking().Where(x => x.Id == id))
            .FirstOrDefaultAsync(ct)
            ?? throw new EntityNotFoundException(nameof(ProductionBatch), id);
    }

    public async Task<ProductionBatchDto> CreatePlanAsync(CreateProductionPlanRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Baker);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(planValidator, request, ct);

        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProductId, ct)
            ?? throw new EntityNotFoundException(nameof(Product), request.ProductId);

        if (product.IsArchived)
        {
            throw new DomainValidationException($"Cannot plan production for archived product '{product.Name}'.");
        }

        var activeRecipe = await db.Recipes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProductId == product.Id && r.IsActive, ct);

        var batch = new ProductionBatch
        {
            BatchReference = $"PRD-{request.ProductionDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            ProductId = product.Id,
            RecipeId = activeRecipe?.Id,
            RecipeVersionSnapshot = activeRecipe?.VersionNumber,
            PlannedQuantity = decimal.Round(request.PlannedQuantity, 4, MidpointRounding.AwayFromZero),
            ActualQuantity = 0m,
            ProductionDate = request.ProductionDate,
            StartedAt = clock.UtcNow,
            BakerUserId = userId,
            Status = ProductionBatchStatus.Planned,
            Remarks = request.Remarks.Trim()
        };

        db.ProductionBatches.Add(batch);
        await db.SaveChangesAsync(ct);

        await audit.AddAuditEntryAsync(
            "CreateProductionPlan",
            nameof(ProductionBatch),
            batch.Id.ToString(),
            $"Planned production batch {batch.BatchReference} for '{product.Name}' (Qty: {batch.PlannedQuantity:0.####}).",
            newValues: new { batch.BatchReference, batch.ProductId, batch.PlannedQuantity, batch.RecipeVersionSnapshot },
            ct: ct);
        await db.SaveChangesAsync(ct);

        return await GetByIdAsync(batch.Id, ct);
    }

    public async Task<ProductionBatchDto> FinalizeProductionAsync(FinalizeProductionRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Baker);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(finalizeValidator, request, ct);

        var opConfig = await settingService.GetOperationalConfigAsync(ct);
        var outsideHours = RecipeService.IsOutsideWindow(clock.LocalNow, opConfig.FactoryOpenTime, opConfig.FactoryCloseTime);
        if (outsideHours && opConfig.EnforceOperatingWindow && !currentUser.IsInRole(AppRoles.Owner))
        {
            throw new DomainValidationException(
                $"Production cannot be finalized outside configured factory operating hours ({opConfig.FactoryOpenTime} – {opConfig.FactoryCloseTime}).");
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var idem = request.IdempotencyKey.Trim();
            var alreadyExists = await db.ProductionBatches.AsNoTracking()
                .AnyAsync(b => b.BatchReference == idem, ct);
            if (alreadyExists)
            {
                throw new DuplicateTransactionException(idem);
            }
        }

        var product = await db.Products
            .Include(p => p.BaseUnit)
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, ct)
            ?? throw new EntityNotFoundException(nameof(Product), request.ProductId);

        if (product.IsArchived)
        {
            throw new DomainValidationException($"Cannot produce archived product '{product.Name}'.");
        }

        if (request.Consumptions.Select(c => c.RawMaterialId).Distinct().Count() != request.Consumptions.Count)
        {
            throw new DomainValidationException("Duplicate raw material entries are not allowed in production consumption.");
        }

        long savedBatchId;
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            ProductionBatch batch;
            if (request.ExistingBatchId.HasValue)
            {
                batch = await db.ProductionBatches
                    .FirstOrDefaultAsync(b => b.Id == request.ExistingBatchId.Value, ct)
                    ?? throw new EntityNotFoundException(nameof(ProductionBatch), request.ExistingBatchId.Value);

                if (batch.Status == ProductionBatchStatus.Completed)
                {
                    throw new DuplicateTransactionException(batch.BatchReference);
                }

                if (batch.Status == ProductionBatchStatus.Cancelled)
                {
                    throw new DomainValidationException($"Batch {batch.BatchReference} is cancelled and cannot be finalized.");
                }

                if (batch.ProductId != product.Id)
                {
                    throw new DomainValidationException("Production batch product mismatch.");
                }
            }
            else
            {
                var activeRecipeForNew = await db.Recipes.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.ProductId == product.Id && r.IsActive, ct);

                var batchRef = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                    ? $"PRD-{request.ProductionDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}"
                    : request.IdempotencyKey.Trim();

                batch = new ProductionBatch
                {
                    BatchReference = batchRef,
                    ProductId = product.Id,
                    RecipeId = activeRecipeForNew?.Id,
                    RecipeVersionSnapshot = activeRecipeForNew?.VersionNumber,
                    PlannedQuantity = decimal.Round(request.PlannedQuantity, 4, MidpointRounding.AwayFromZero),
                    ProductionDate = request.ProductionDate,
                    StartedAt = clock.UtcNow,
                    BakerUserId = userId
                };
                db.ProductionBatches.Add(batch);
            }

            Recipe? recipe = null;
            if (batch.RecipeId.HasValue)
            {
                recipe = await db.Recipes.AsNoTracking()
                    .Include(r => r.YieldUnit)
                    .Include(r => r.Ingredients).ThenInclude(i => i.Unit)
                    .Include(r => r.Ingredients).ThenInclude(i => i.RawMaterial).ThenInclude(m => m.Unit)
                    .FirstOrDefaultAsync(r => r.Id == batch.RecipeId.Value, ct);
            }
            else
            {
                recipe = await db.Recipes.AsNoTracking()
                    .Include(r => r.YieldUnit)
                    .Include(r => r.Ingredients).ThenInclude(i => i.Unit)
                    .Include(r => r.Ingredients).ThenInclude(i => i.RawMaterial).ThenInclude(m => m.Unit)
                    .FirstOrDefaultAsync(r => r.ProductId == product.Id && r.IsActive, ct);
                if (recipe is not null)
                {
                    batch.RecipeId = recipe.Id;
                    batch.RecipeVersionSnapshot = recipe.VersionNumber;
                }
            }

            var plannedByMaterialInBase = new Dictionary<int, decimal>();
            if (recipe is not null)
            {
                var targetInYield = UnitConversionHelper.Convert(request.PlannedQuantity, product.BaseUnit, recipe.YieldUnit);
                var scale = targetInYield / recipe.BatchYieldQuantity;
                foreach (var ing in recipe.Ingredients)
                {
                    var scaledIng = decimal.Round(ing.QuantityRequired * scale, 4, MidpointRounding.AwayFromZero);
                    var plannedBase = UnitConversionHelper.Convert(scaledIng, ing.Unit, ing.RawMaterial.Unit, ing.RawMaterial.PackToBaseRatio);
                    plannedByMaterialInBase[ing.RawMaterialId] = plannedBase;
                }

                if (request.Consumptions.Count == 0)
                {
                    throw new DomainValidationException("Ingredient consumption must be reviewed and confirmed against the active recipe before finalizing production.");
                }

                var suppliedIds = request.Consumptions.Select(c => c.RawMaterialId).ToHashSet();
                var missingRecipeItems = plannedByMaterialInBase.Keys.Where(id => !suppliedIds.Contains(id)).ToList();
                if (missingRecipeItems.Count > 0)
                {
                    throw new DomainValidationException("All active recipe ingredients must be confirmed in production consumption.");
                }
            }
            else if (request.Consumptions.Count == 0 && !request.ConfirmNoRecipe)
            {
                throw new DomainValidationException("No active recipe is configured for this product. Confirm ingredient consumption or explicitly set ConfirmNoRecipe.");
            }

            var consumedIds = request.Consumptions.Select(c => c.RawMaterialId).ToList();
            var unitIds = request.Consumptions.Select(c => c.UnitId).Distinct().ToList();

            var materials = await db.RawMaterials
                .Include(m => m.Unit)
                .Where(m => consumedIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, ct);

            var units = await db.UnitsOfMeasure.AsNoTracking()
                .Where(u => unitIds.Contains(u.Id) && u.IsActive)
                .ToDictionaryAsync(u => u.Id, ct);

            batch.PlannedQuantity = decimal.Round(request.PlannedQuantity, 4, MidpointRounding.AwayFromZero);
            batch.ActualQuantity = decimal.Round(request.ActualOutputQuantity, 4, MidpointRounding.AwayFromZero);
            batch.ProductionDate = request.ProductionDate;
            batch.CompletedAt = clock.UtcNow;
            batch.BakerUserId = userId;
            batch.Status = ProductionBatchStatus.Completed;
            batch.Remarks = request.Remarks.Trim();

            await db.SaveChangesAsync(ct);
            savedBatchId = batch.Id;

            foreach (var input in request.Consumptions)
            {
                if (!materials.TryGetValue(input.RawMaterialId, out var material) || material.IsArchived)
                {
                    throw new DomainValidationException($"Raw material ({input.RawMaterialId}) does not exist or is archived.");
                }

                if (!units.TryGetValue(input.UnitId, out var inputUnit))
                {
                    throw new DomainValidationException($"Unit ({input.UnitId}) does not exist.");
                }

                var consumedInBase = UnitConversionHelper.Convert(
                    input.ActualConsumedQuantity,
                    inputUnit,
                    material.Unit,
                    material.PackToBaseRatio);

                var plannedBase = plannedByMaterialInBase.GetValueOrDefault(material.Id, consumedInBase);

                db.ProductionConsumptions.Add(new ProductionConsumption
                {
                    ProductionBatchId = batch.Id,
                    RawMaterialId = material.Id,
                    PlannedQuantity = plannedBase,
                    ActualConsumedQuantity = consumedInBase,
                    UnitId = material.UnitId,
                    SnapshotRawMaterialName = material.Name,
                    SnapshotRecipeQuantity = plannedBase
                });

                await ledger.RecordRawMaterialMovementAsync(new RecordRawMaterialMovementRequest(
                    RawMaterialId: material.Id,
                    QuantityDelta: -consumedInBase,
                    UnitId: material.UnitId,
                    SourceLocation: InventoryLocation.Factory,
                    DestinationLocation: InventoryLocation.Factory,
                    MovementType: InventoryMovementType.ProductionConsumption,
                    ReferenceType: nameof(ProductionBatch),
                    ReferenceId: batch.Id.ToString(),
                    ReferenceNumber: batch.BatchReference,
                    Remarks: $"Consumed for {product.Name} ({batch.BatchReference}).",
                    DeduplicationKey: $"ProductionConsumption:Batch:{batch.Id}:RM:{material.Id}"), ct);
            }

            var resolvedExpiry = request.ExpirationDate
                ?? (product.ShelfLifeDays.HasValue ? request.ProductionDate.AddDays(product.ShelfLifeDays.Value) : null);

            var lot = new FactoryStockLot
            {
                LotNumber = $"LOT-{request.ProductionDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
                ProductId = product.Id,
                ProductionBatchId = batch.Id,
                ProductionDate = request.ProductionDate,
                ExpirationDate = resolvedExpiry,
                InitialQuantity = batch.ActualQuantity,
                RemainingQuantity = 0m,
                ReservedQuantity = 0m,
                Status = FactoryLotStatus.Available,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            };
            db.FactoryStockLots.Add(lot);
            await db.SaveChangesAsync(ct);

            await ledger.RecordFactoryLotMovementAsync(new RecordFactoryLotMovementRequest(
                FactoryStockLotId: lot.Id,
                QuantityDelta: batch.ActualQuantity,
                SourceLocation: InventoryLocation.Factory,
                DestinationLocation: InventoryLocation.Factory,
                MovementType: InventoryMovementType.ProductionOutput,
                ReferenceType: nameof(ProductionBatch),
                ReferenceId: batch.Id.ToString(),
                ReferenceNumber: batch.BatchReference,
                Remarks: $"Finished goods lot {lot.LotNumber} produced by batch {batch.BatchReference}.",
                DeduplicationKey: $"ProductionOutput:Batch:{batch.Id}:LOT:{lot.Id}"), ct);

            var hoursNote = outsideHours ? " [Outside configured factory hours]" : string.Empty;
            await audit.AddAuditEntryAsync(
                "FinalizeProduction",
                nameof(ProductionBatch),
                batch.Id.ToString(),
                $"Finalized production {batch.BatchReference} for '{product.Name}' (Qty: {batch.ActualQuantity:0.####} {product.BaseUnit.Code}, Lot: {lot.LotNumber}).{hoursNote}",
                newValues: new { batch.BatchReference, batch.ProductId, batch.ActualQuantity, lot.LotNumber, batch.RecipeVersionSnapshot },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(savedBatchId, ct);
    }

    public async Task CancelPlanAsync(long batchId, string reason, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Baker);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException("A cancellation reason is required.");
        }

        var batch = await db.ProductionBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new EntityNotFoundException(nameof(ProductionBatch), batchId);

        if (batch.Status is ProductionBatchStatus.Completed or ProductionBatchStatus.Cancelled)
        {
            throw new DomainValidationException($"Batch {batch.BatchReference} is already {batch.Status} and cannot be cancelled.");
        }

        var prev = batch.Status;
        batch.Status = ProductionBatchStatus.Cancelled;
        batch.Remarks = string.IsNullOrWhiteSpace(batch.Remarks) ? $"Cancelled: {reason.Trim()}" : $"{batch.Remarks} | Cancelled: {reason.Trim()}";

        await audit.AddAuditEntryAsync(
            "CancelProductionPlan",
            nameof(ProductionBatch),
            batch.Id.ToString(),
            $"Cancelled production plan {batch.BatchReference}. Reason: {reason.Trim()}",
            oldValues: new { Status = prev },
            newValues: new { Status = ProductionBatchStatus.Cancelled },
            ct: ct);

        await db.SaveChangesAsync(ct);
    }

    private static IQueryable<ProductionBatchDto> ProjectBatches(IQueryable<ProductionBatch> source) =>
        source.Select(b => new ProductionBatchDto(
            b.Id,
            b.BatchReference,
            b.ProductId,
            b.Product.Sku,
            b.Product.Name,
            b.RecipeId,
            b.RecipeVersionSnapshot,
            b.PlannedQuantity,
            b.ActualQuantity,
            b.Product.BaseUnit.Code,
            b.ProductionDate,
            b.StartedAt,
            b.CompletedAt,
            b.BakerUserId,
            b.BakerUser.FullName,
            b.Status,
            b.Remarks,
            b.Consumptions.Select(c => new ProductionConsumptionDto(
                c.Id,
                c.RawMaterialId,
                c.SnapshotRawMaterialName,
                c.PlannedQuantity,
                c.ActualConsumedQuantity,
                c.UnitId,
                c.Unit.Code,
                c.SnapshotRecipeQuantity)).ToList()));
}

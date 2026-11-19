using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface ISupplierService
{
    Task<PagedResult<SupplierDto>> GetPagedAsync(string? searchTerm = null, bool includeInactive = false, int pageNumber = 1, int pageSize = 25, CancellationToken ct = default);
    Task<SupplierDto> GetByIdAsync(int id, CancellationToken ct = default);
    Task<SupplierDto> SaveAsync(SaveSupplierRequest request, CancellationToken ct = default);
    Task SetStatusAsync(int supplierId, SupplierStatus status, CancellationToken ct = default);
}

public sealed class SupplierService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IAuditLogService audit,
    IDateTimeProvider clock,
    IValidator<SaveSupplierRequest> validator) : ISupplierService
{
    public async Task<PagedResult<SupplierDto>> GetPagedAsync(
        string? searchTerm = null,
        bool includeInactive = false,
        int pageNumber = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        var page = Math.Max(1, pageNumber);
        var size = Math.Clamp(pageSize, 1, 200);

        var q = db.Suppliers.AsNoTracking();
        if (!includeInactive)
        {
            q = q.Where(x => x.Status == SupplierStatus.Active);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            q = q.Where(x => x.Name.Contains(term) || x.ContactPerson.Contains(term) || x.Phone.Contains(term));
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderBy(x => x.Name)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(x => new SupplierDto(
                x.Id,
                x.Name,
                x.ContactPerson,
                x.Phone,
                x.Email,
                x.Address,
                x.Status,
                x.Notes,
                x.CreatedAt,
                x.UpdatedAt,
                x.SuppliedMaterials.Select(sm => new SupplierMaterialDto(
                    sm.RawMaterialId,
                    sm.RawMaterial.Sku,
                    sm.RawMaterial.Name,
                    sm.RawMaterial.Unit.Code,
                    sm.DefaultUnitCost,
                    sm.LeadTimeDays)).ToList()))
            .ToListAsync(ct);

        return new PagedResult<SupplierDto>(items, total, page, size);
    }

    public async Task<SupplierDto> GetByIdAsync(int id, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        return await db.Suppliers.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new SupplierDto(
                x.Id,
                x.Name,
                x.ContactPerson,
                x.Phone,
                x.Email,
                x.Address,
                x.Status,
                x.Notes,
                x.CreatedAt,
                x.UpdatedAt,
                x.SuppliedMaterials.Select(sm => new SupplierMaterialDto(
                    sm.RawMaterialId,
                    sm.RawMaterial.Sku,
                    sm.RawMaterial.Name,
                    sm.RawMaterial.Unit.Code,
                    sm.DefaultUnitCost,
                    sm.LeadTimeDays)).ToList()))
            .FirstOrDefaultAsync(ct)
            ?? throw new EntityNotFoundException(nameof(Supplier), id);
    }

    public async Task<SupplierDto> SaveAsync(SaveSupplierRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);
        await ValidationHelper.ValidateAndThrowAsync(validator, request, ct);

        var normalizedName = request.Name.Trim();
        var duplicateName = await db.Suppliers.AsNoTracking()
            .AnyAsync(x => x.Name.ToLower() == normalizedName.ToLower() && (!request.Id.HasValue || x.Id != request.Id.Value), ct);
        if (duplicateName)
        {
            throw new DomainValidationException($"A supplier named '{normalizedName}' already exists.");
        }

        if (request.SuppliedMaterials is not null)
        {
            var requestedIds = request.SuppliedMaterials.Select(m => m.RawMaterialId).ToList();
            if (requestedIds.Count != requestedIds.Distinct().Count())
            {
                throw new DomainValidationException("Duplicate raw materials cannot be assigned to the same supplier.");
            }

            if (requestedIds.Count > 0)
            {
                var foundCount = await db.RawMaterials.AsNoTracking().CountAsync(x => requestedIds.Contains(x.Id), ct);
                if (foundCount != requestedIds.Count)
                {
                    throw new DomainValidationException("One or more supplied raw materials do not exist.");
                }
            }
        }

        Supplier entity;
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            if (!request.Id.HasValue || request.Id.Value == 0)
            {
                entity = new Supplier
                {
                    Name = normalizedName,
                    ContactPerson = request.ContactPerson.Trim(),
                    Phone = request.Phone.Trim(),
                    Email = request.Email.Trim(),
                    Address = request.Address.Trim(),
                    Status = request.Status,
                    Notes = request.Notes.Trim(),
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow
                };

                if (request.SuppliedMaterials is not null)
                {
                    foreach (var mat in request.SuppliedMaterials)
                    {
                        entity.SuppliedMaterials.Add(new SupplierRawMaterial
                        {
                            RawMaterialId = mat.RawMaterialId,
                            DefaultUnitCost = mat.DefaultUnitCost,
                            LeadTimeDays = mat.LeadTimeDays
                        });
                    }
                }

                db.Suppliers.Add(entity);
                await db.SaveChangesAsync(ct);

                await audit.AddAuditEntryAsync(
                    "CreateSupplier",
                    nameof(Supplier),
                    entity.Id.ToString(),
                    $"Created supplier '{entity.Name}'.",
                    newValues: new { entity.Name, entity.Status, SuppliedCount = entity.SuppliedMaterials.Count },
                    ct: ct);
            }
            else
            {
                entity = await db.Suppliers
                    .Include(x => x.SuppliedMaterials)
                    .FirstOrDefaultAsync(x => x.Id == request.Id.Value, ct)
                    ?? throw new EntityNotFoundException(nameof(Supplier), request.Id.Value);

                var oldSnapshot = new { entity.Name, entity.ContactPerson, entity.Phone, entity.Status };

                entity.Name = normalizedName;
                entity.ContactPerson = request.ContactPerson.Trim();
                entity.Phone = request.Phone.Trim();
                entity.Email = request.Email.Trim();
                entity.Address = request.Address.Trim();
                entity.Status = request.Status;
                entity.Notes = request.Notes.Trim();
                entity.UpdatedAt = clock.UtcNow;

                if (request.SuppliedMaterials is not null)
                {
                    entity.SuppliedMaterials.Clear();
                    foreach (var mat in request.SuppliedMaterials)
                    {
                        entity.SuppliedMaterials.Add(new SupplierRawMaterial
                        {
                            SupplierId = entity.Id,
                            RawMaterialId = mat.RawMaterialId,
                            DefaultUnitCost = mat.DefaultUnitCost,
                            LeadTimeDays = mat.LeadTimeDays
                        });
                    }
                }

                await audit.AddAuditEntryAsync(
                    "UpdateSupplier",
                    nameof(Supplier),
                    entity.Id.ToString(),
                    $"Updated supplier '{entity.Name}'.",
                    oldValues: oldSnapshot,
                    newValues: new { entity.Name, entity.ContactPerson, entity.Phone, entity.Status },
                    ct: ct);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(entity.Id, ct);
    }

    public async Task SetStatusAsync(int supplierId, SupplierStatus status, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);

        var entity = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == supplierId, ct)
            ?? throw new EntityNotFoundException(nameof(Supplier), supplierId);

        if (entity.Status == status)
        {
            return;
        }

        var oldStatus = entity.Status;
        entity.Status = status;
        entity.UpdatedAt = clock.UtcNow;

        await audit.AddAuditEntryAsync(
            "UpdateSupplierStatus",
            nameof(Supplier),
            entity.Id.ToString(),
            $"Changed supplier '{entity.Name}' status from {oldStatus} to {status}.",
            oldValues: new { Status = oldStatus },
            newValues: new { Status = status },
            ct: ct);

        await db.SaveChangesAsync(ct);
    }
}

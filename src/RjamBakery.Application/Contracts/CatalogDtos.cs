namespace RjamBakery.Application.Contracts;

public sealed record CategoryDto(
    int Id,
    string Name,
    string Description,
    int DisplayOrder,
    bool IsActive,
    int ProductCount);

public sealed record SaveCategoryRequest(
    int? Id,
    string Name,
    string Description,
    int DisplayOrder,
    bool IsActive = true);

public sealed record ProductDto(
    int Id,
    int CategoryId,
    string CategoryName,
    string Sku,
    string Name,
    string Description,
    int BaseUnitId,
    string BaseUnitCode,
    decimal SellingPrice,
    decimal MinStockLevel,
    int? ShelfLifeDays,
    string? ImagePath,
    bool IsArchived,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SaveProductRequest(
    int? Id,
    int CategoryId,
    string? Sku,
    string Name,
    string Description,
    int BaseUnitId,
    decimal SellingPrice,
    decimal MinStockLevel,
    int? ShelfLifeDays,
    string? ImagePath);

public sealed record ProductQuery(
    string? SearchTerm = null,
    int? CategoryId = null,
    bool IncludeArchived = false,
    int PageNumber = 1,
    int PageSize = 25);

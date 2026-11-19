namespace RjamBakery.Application.Contracts;

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageNumber,
    int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public record PagedQuery(int PageNumber = 1, int PageSize = 25);

public sealed record UnitOfMeasureDto(
    int Id,
    string Code,
    string Name,
    string Dimension,
    decimal FactorToBase,
    bool IsBaseUnit);

namespace RjamBakery.Domain.Common;

public abstract class DomainException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class DomainValidationException(string message)
    : DomainException(message, "VALIDATION_ERROR");

public sealed class EntityNotFoundException(string entityName, object key)
    : DomainException($"{entityName} ({key}) was not found.", "NOT_FOUND");

public sealed class InsufficientStockException(string itemName, decimal available, decimal required, string unitCode)
    : DomainException(
        $"Insufficient stock for '{itemName}'. Available: {available:0.####} {unitCode}, Required: {required:0.####} {unitCode}.",
        "INSUFFICIENT_STOCK");

public sealed class DuplicateTransactionException(string reference)
    : DomainException($"Duplicate transaction detected for reference '{reference}'.", "DUPLICATE_TRANSACTION");

public sealed class ConcurrencyConflictException(string message)
    : DomainException(message, "CONCURRENCY_CONFLICT");

public sealed class UnauthorizedDomainActionException(string message)
    : DomainException(message, "FORBIDDEN");

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;

namespace RjamBakery.Application.Services;

public interface IAuditLogService
{
    Task AddAuditEntryAsync(
        string action,
        string entityName,
        string entityId,
        string summary,
        object? oldValues = null,
        object? newValues = null,
        string? explicitUserId = null,
        string? explicitUserName = null,
        string? explicitUserRole = null,
        CancellationToken ct = default);

    Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken ct = default);
}

public sealed class AuditLogService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock) : IAuditLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task AddAuditEntryAsync(
        string action,
        string entityName,
        string entityId,
        string summary,
        object? oldValues = null,
        object? newValues = null,
        string? explicitUserId = null,
        string? explicitUserName = null,
        string? explicitUserRole = null,
        CancellationToken ct = default)
    {
        var userId = explicitUserId ?? currentUser.UserId ?? throw new UnauthorizedDomainActionException("Authenticated user context is required to write audit logs.");
        var userName = explicitUserName ?? (string.IsNullOrWhiteSpace(currentUser.UserName) ? "System" : currentUser.UserName);
        var userRole = explicitUserRole ?? (string.IsNullOrWhiteSpace(currentUser.PrimaryRole) ? "System" : currentUser.PrimaryRole);

        var entry = new AuditLog
        {
            UserId = userId,
            UserName = userName,
            UserRole = userRole,
            Action = action.Trim(),
            EntityName = entityName.Trim(),
            EntityId = entityId.Trim(),
            Summary = summary.Trim(),
            OldValuesJson = oldValues is null ? null : JsonSerializer.Serialize(oldValues, JsonOptions),
            NewValuesJson = newValues is null ? null : JsonSerializer.Serialize(newValues, JsonOptions),
            IpAddress = currentUser.IpAddress,
            CreatedAt = clock.UtcNow
        };

        db.AuditLogs.Add(entry);
        return Task.CompletedTask;
    }

    public async Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        var page = Math.Max(1, query.PageNumber);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var q = db.AuditLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.UserId))
        {
            q = q.Where(x => x.UserId == query.UserId);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var act = query.Action.Trim();
            q = q.Where(x => x.Action.Contains(act));
        }

        if (!string.IsNullOrWhiteSpace(query.EntityName))
        {
            var ent = query.EntityName.Trim();
            q = q.Where(x => x.EntityName == ent);
        }

        if (query.FromUtc.HasValue)
        {
            q = q.Where(x => x.CreatedAt >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            q = q.Where(x => x.CreatedAt <= query.ToUtc.Value);
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AuditLogDto(
                x.Id,
                x.UserId,
                x.UserName,
                x.UserRole,
                x.Action,
                x.EntityName,
                x.EntityId,
                x.Summary,
                x.OldValuesJson,
                x.NewValuesJson,
                x.IpAddress,
                x.CreatedAt))
            .ToListAsync(ct);

        return new PagedResult<AuditLogDto>(items, total, page, pageSize);
    }
}

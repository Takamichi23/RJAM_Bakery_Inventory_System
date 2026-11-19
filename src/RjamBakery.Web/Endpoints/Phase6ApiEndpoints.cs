using Microsoft.AspNetCore.Mvc;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Enums;
using RjamBakery.Web.Security;

namespace RjamBakery.Web.Endpoints;

public static class Phase6ApiEndpoints
{
    private const int MaxReportSpanDays = 366 * 5;

    private static void ValidateDateRange(DateOnly from, DateOnly to)
    {
        if (from > to)
        {
            throw new DomainValidationException("Start date cannot be later than end date.");
        }

        if (to.DayNumber - from.DayNumber > MaxReportSpanDays)
        {
            throw new DomainValidationException("Report date range cannot exceed 5 years.");
        }
    }

    private static void ValidateDateRange(DateTime fromUtc, DateTime toUtc)
    {
        if (fromUtc > toUtc)
        {
            throw new DomainValidationException("Start timestamp cannot be later than end timestamp.");
        }

        if ((toUtc - fromUtc).TotalDays > MaxReportSpanDays)
        {
            throw new DomainValidationException("Report date range cannot exceed 5 years.");
        }
    }

    public static IEndpointRouteBuilder MapPhase6ApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .RequireRateLimiting(SecurityOptions.ApiRateLimitPolicy);

        // Returns, Damages, and Spoilage
        var waste = api.MapGroup("/waste-returns").RequireAuthorization();
        waste.MapGet("/", async (
            WasteReturnCategory? category,
            InspectionOutcome? outcome,
            InventoryLocation? location,
            int? page,
            int? pageSize,
            IWasteAndReturnService service,
            CancellationToken ct)
            => Results.Ok(await service.GetPagedAsync(
                new WasteAndReturnQuery(category, outcome, location, null, null, page ?? 1, pageSize ?? 50), ct)));

        waste.MapGet("/{id:long}", async (long id, IWasteAndReturnService service, CancellationToken ct)
            => Results.Ok(await service.GetByIdAsync(id, ct)));

        waste.MapPost("/", async ([FromBody] RecordWasteOrReturnRequest request, IWasteAndReturnService service, CancellationToken ct)
            => Results.Ok(await service.RecordAsync(request, ct)))
            .RequireAuthorization(AppPolicies.RecordWasteOrReturn);

        waste.MapPost("/{id:long}/inspect", async (long id, [FromBody] InspectCustomerReturnRequest request, IWasteAndReturnService service, CancellationToken ct)
            => Results.Ok(await service.InspectCustomerReturnAsync(id, request, ct)))
            .RequireAuthorization(AppPolicies.ApproveAdjustmentsAndVariances);

        // Daily Closing & Carryover
        var closing = api.MapGroup("/daily-closing").RequireAuthorization();
        closing.MapGet("/preview", async (DateOnly? date, IDailyClosingService service, CancellationToken ct)
            => Results.Ok(await service.PreviewClosingAsync(date ?? DateOnly.FromDateTime(DateTime.Now), ct)))
            .RequireAuthorization(AppPolicies.SubmitDailyClosing);

        closing.MapPost("/submit", async ([FromBody] SubmitDailyClosingRequest request, IDailyClosingService service, CancellationToken ct)
            => Results.Ok(await service.SubmitPhysicalCountAsync(request, ct)))
            .RequireAuthorization(AppPolicies.SubmitDailyClosing);

        closing.MapPost("/{id:long}/finalize", async (long id, [FromBody] FinalizeDailyClosingRequest request, IDailyClosingService service, CancellationToken ct)
            => Results.Ok(await service.ApproveAndFinalizeAsync(id, request, ct)))
            .RequireAuthorization(AppPolicies.ApproveAdjustmentsAndVariances);

        closing.MapGet("/history", async (DateOnly? fromDate, DateOnly? toDate, int? page, int? pageSize, IDailyClosingService service, CancellationToken ct)
            => Results.Ok(await service.GetHistoryAsync(fromDate, toDate, page ?? 1, pageSize ?? 25, ct)))
            .RequireAuthorization(AppPolicies.SubmitDailyClosing);

        // Reports & Exports
        var reports = api.MapGroup("/reports").RequireAuthorization(AppPolicies.ViewReports);

        reports.MapGet("/inventory", async (IReportingService service, CancellationToken ct)
            => Results.Ok(await service.GetInventoryReportAsync(ct)));

        reports.MapGet("/production", async (DateOnly? fromDate, DateOnly? toDate, int? productId, IReportingService service, CancellationToken ct) =>
        {
            var to = toDate ?? DateOnly.FromDateTime(DateTime.Now);
            var from = fromDate ?? to.AddDays(-7);
            ValidateDateRange(from, to);
            return Results.Ok(await service.GetProductionReportAsync(from, to, productId, ct));
        });

        reports.MapGet("/sales", async (DateTime? fromUtc, DateTime? toUtc, int? productId, IReportingService service, CancellationToken ct) =>
        {
            var to = toUtc ?? DateTime.UtcNow;
            var from = fromUtc ?? to.AddDays(-7);
            ValidateDateRange(from, to);
            return Results.Ok(await service.GetSalesReportAsync(from, to, productId, ct));
        });

        reports.MapGet("/deliveries", async (DateTime? fromUtc, DateTime? toUtc, IReportingService service, CancellationToken ct) =>
        {
            var to = toUtc ?? DateTime.UtcNow;
            var from = fromUtc ?? to.AddDays(-7);
            ValidateDateRange(from, to);
            return Results.Ok(await service.GetDeliveryReportAsync(from, to, ct));
        });

        reports.MapGet("/closing", async (DateOnly? fromDate, DateOnly? toDate, IReportingService service, CancellationToken ct) =>
        {
            var to = toDate ?? DateOnly.FromDateTime(DateTime.Now);
            var from = fromDate ?? to.AddDays(-7);
            ValidateDateRange(from, to);
            return Results.Ok(await service.GetClosingReportAsync(from, to, ct));
        });

        reports.MapGet("/inventory/export", async (string? format, IReportingService reporting, IReportExportService exporter, CancellationToken ct) =>
        {
            var report = await reporting.GetInventoryReportAsync(ct);
            var file = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase)
                ? exporter.ExportInventoryPdf(report)
                : exporter.ExportInventoryExcel(report);
            return Results.File(file.Content, file.ContentType, file.FileName);
        }).RequireRateLimiting(SecurityOptions.ExportRateLimitPolicy);

        reports.MapGet("/production/export", async (string? format, DateOnly? fromDate, DateOnly? toDate, int? productId, IReportingService reporting, IReportExportService exporter, CancellationToken ct) =>
        {
            var to = toDate ?? DateOnly.FromDateTime(DateTime.Now);
            var from = fromDate ?? to.AddDays(-7);
            ValidateDateRange(from, to);
            var report = await reporting.GetProductionReportAsync(from, to, productId, ct);
            var file = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase)
                ? exporter.ExportProductionPdf(report)
                : exporter.ExportProductionExcel(report);
            return Results.File(file.Content, file.ContentType, file.FileName);
        }).RequireRateLimiting(SecurityOptions.ExportRateLimitPolicy);

        reports.MapGet("/sales/export", async (string? format, DateTime? fromUtc, DateTime? toUtc, int? productId, IReportingService reporting, IReportExportService exporter, CancellationToken ct) =>
        {
            var to = toUtc ?? DateTime.UtcNow;
            var from = fromUtc ?? to.AddDays(-7);
            ValidateDateRange(from, to);
            var report = await reporting.GetSalesReportAsync(from, to, productId, ct);
            var file = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase)
                ? exporter.ExportSalesPdf(report)
                : exporter.ExportSalesExcel(report);
            return Results.File(file.Content, file.ContentType, file.FileName);
        }).RequireRateLimiting(SecurityOptions.ExportRateLimitPolicy);

        reports.MapGet("/deliveries/export", async (string? format, DateTime? fromUtc, DateTime? toUtc, IReportingService reporting, IReportExportService exporter, CancellationToken ct) =>
        {
            var to = toUtc ?? DateTime.UtcNow;
            var from = fromUtc ?? to.AddDays(-7);
            ValidateDateRange(from, to);
            var report = await reporting.GetDeliveryReportAsync(from, to, ct);
            var file = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase)
                ? exporter.ExportDeliveryPdf(report)
                : exporter.ExportDeliveryExcel(report);
            return Results.File(file.Content, file.ContentType, file.FileName);
        }).RequireRateLimiting(SecurityOptions.ExportRateLimitPolicy);

        reports.MapGet("/closing/export", async (string? format, DateOnly? fromDate, DateOnly? toDate, IReportingService reporting, IReportExportService exporter, CancellationToken ct) =>
        {
            var to = toDate ?? DateOnly.FromDateTime(DateTime.Now);
            var from = fromDate ?? to.AddDays(-7);
            ValidateDateRange(from, to);
            var report = await reporting.GetClosingReportAsync(from, to, ct);
            var file = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase)
                ? exporter.ExportClosingPdf(report)
                : exporter.ExportClosingExcel(report);
            return Results.File(file.Content, file.ContentType, file.FileName);
        }).RequireRateLimiting(SecurityOptions.ExportRateLimitPolicy);

        return app;
    }
}

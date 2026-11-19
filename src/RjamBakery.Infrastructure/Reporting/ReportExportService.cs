using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;

namespace RjamBakery.Infrastructure.Reporting;

public sealed class ReportExportService : IReportExportService
{
    static ReportExportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public ReportFileResult ExportInventoryExcel(InventoryReportDto report)
    {
        using var wb = new XLWorkbook();
        AddSheet(wb, "Store Inventory",
            ["SKU", "Product", "Category", "Current Qty", "Min Level", "Unit", "Status"],
            report.StoreInventory.Select(s => new object?[] { s.ProductSku, s.ProductName, s.CategoryName, s.CurrentQuantity, s.MinStockLevel, s.UnitCode, s.StockStatus }));

        AddSheet(wb, "Factory Summary",
            ["SKU", "Product", "Remaining", "Reserved", "Available", "Min Level", "Unit", "Status"],
            report.FactorySummary.Select(f => new object?[] { f.ProductSku, f.ProductName, f.TotalRemainingQuantity, f.TotalReservedQuantity, f.TotalAvailableQuantity, f.MinStockLevel, f.UnitCode, f.StockStatus }));

        AddSheet(wb, "Raw Materials",
            ["SKU", "Material", "Current Qty", "Min Level", "Unit", "Status"],
            report.RawMaterials.Select(m => new object?[] { m.Sku, m.Name, m.CurrentQuantity, m.MinStockLevel, m.UnitCode, m.StockStatus }));

        AddSheet(wb, "Low & Out Alerts",
            ["Location", "SKU", "Item", "Available", "Min Level", "Unit", "Status"],
            report.LowAndOutOfStockItems.Select(a => new object?[] { a.Bucket, a.Sku, a.Name, a.AvailableQuantity, a.MinStockLevel, a.UnitCode, a.StockStatus }));

        return ToExcelResult(wb, $"RJAM-Inventory-Report-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }

    public ReportFileResult ExportInventoryPdf(InventoryReportDto report)
    {
        var bytes = BuildPdf("RJAM Bakery — Inventory Report", $"Generated {report.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC", col =>
        {
            col.Item().PaddingBottom(8).Text("Store Inventory").Bold().FontSize(11);
            AddPdfTable(col,
                ["SKU", "Product", "Qty", "Min", "Unit", "Status"],
                report.StoreInventory.Select(s => new[] { s.ProductSku, s.ProductName, s.CurrentQuantity.ToString("0.##"), s.MinStockLevel.ToString("0.##"), s.UnitCode, s.StockStatus }));

            col.Item().PaddingTop(14).PaddingBottom(8).Text("Raw Material Inventory").Bold().FontSize(11);
            AddPdfTable(col,
                ["SKU", "Material", "Qty", "Min", "Unit", "Status"],
                report.RawMaterials.Select(m => new[] { m.Sku, m.Name, m.CurrentQuantity.ToString("0.##"), m.MinStockLevel.ToString("0.##"), m.UnitCode, m.StockStatus }));
        });
        return ToPdfResult(bytes, $"RJAM-Inventory-Report-{DateTime.UtcNow:yyyyMMdd}.pdf");
    }

    public ReportFileResult ExportProductionExcel(ProductionReportDto report)
    {
        using var wb = new XLWorkbook();
        AddSheet(wb, "Production Summary",
            ["SKU", "Product", "Batches", "Planned Qty", "Actual Qty", "Unit"],
            report.ProductSummaries.Select(p => new object?[] { p.ProductSku, p.ProductName, p.BatchCount, p.TotalPlannedQuantity, p.TotalActualQuantity, p.UnitCode }));

        AddSheet(wb, "Ingredient Consumption",
            ["SKU", "Material", "Planned Consumed", "Actual Consumed", "Unit"],
            report.IngredientConsumptions.Select(i => new object?[] { i.RawMaterialSku, i.RawMaterialName, i.TotalPlannedConsumed, i.TotalActualConsumed, i.UnitCode }));

        return ToExcelResult(wb, $"RJAM-Production-Report-{report.FromDate:yyyyMMdd}-{report.ToDate:yyyyMMdd}.xlsx");
    }

    public ReportFileResult ExportProductionPdf(ProductionReportDto report)
    {
        var bytes = BuildPdf("RJAM Bakery — Production Report", $"Period: {report.FromDate:yyyy-MM-dd} to {report.ToDate:yyyy-MM-dd} | Batches: {report.CompletedBatches}", col =>
        {
            col.Item().PaddingBottom(8).Text("Product Production Summary").Bold().FontSize(11);
            AddPdfTable(col,
                ["SKU", "Product", "Batches", "Planned", "Actual", "Unit"],
                report.ProductSummaries.Select(p => new[] { p.ProductSku, p.ProductName, p.BatchCount.ToString(), p.TotalPlannedQuantity.ToString("0.##"), p.TotalActualQuantity.ToString("0.##"), p.UnitCode }));

            col.Item().PaddingTop(14).PaddingBottom(8).Text("Ingredient Consumption").Bold().FontSize(11);
            AddPdfTable(col,
                ["SKU", "Ingredient", "Planned", "Actual", "Unit"],
                report.IngredientConsumptions.Select(i => new[] { i.RawMaterialSku, i.RawMaterialName, i.TotalPlannedConsumed.ToString("0.####"), i.TotalActualConsumed.ToString("0.####"), i.UnitCode }));
        });
        return ToPdfResult(bytes, $"RJAM-Production-Report-{report.FromDate:yyyyMMdd}-{report.ToDate:yyyyMMdd}.pdf");
    }

    public ReportFileResult ExportSalesExcel(SalesReportDto report)
    {
        using var wb = new XLWorkbook();
        AddSheet(wb, "Sales Overview",
            ["Metric", "Value"],
            new object?[][]
            {
                ["Completed Transactions", report.CompletedCount],
                ["Gross Subtotal Revenue", report.GrossSubTotal],
                ["Discounts", report.TotalDiscounts],
                ["Net Sales Revenue (Excludes COGS/Expenses)", report.NetSalesRevenue],
                ["Voided Transactions", report.VoidedCount],
                ["Voided Amount", report.VoidedAmount],
                ["Refunded Transactions", report.RefundedCount],
                ["Refunded Amount", report.RefundedAmount]
            });

        AddSheet(wb, "Best Selling Products",
            ["SKU", "Product", "Qty Sold", "Gross Sales Revenue"],
            report.BestSellingProducts.Select(p => new object?[] { p.Sku, p.ProductName, p.TotalQuantitySold, p.GrossSalesRevenue }));

        AddSheet(wb, "Sales by Cashier",
            ["Cashier", "Completed Count", "Net Sales Revenue"],
            report.SalesByCashier.Select(c => new object?[] { c.CashierName, c.CompletedTransactionCount, c.NetSalesRevenue }));

        return ToExcelResult(wb, $"RJAM-Sales-Report-{report.FromUtc:yyyyMMdd}-{report.ToUtc:yyyyMMdd}.xlsx");
    }

    public ReportFileResult ExportSalesPdf(SalesReportDto report)
    {
        var sym = report.CurrencySymbol;
        var bytes = BuildPdf("RJAM Bakery — Sales Revenue Report",
            $"Period: {report.FromUtc:yyyy-MM-dd} to {report.ToUtc:yyyy-MM-dd} | Net Sales Revenue: {sym}{report.NetSalesRevenue:N2} (Note: Revenue is not Profit)",
            col =>
            {
                col.Item().PaddingBottom(8).Text("Best-Selling Products").Bold().FontSize(11);
                AddPdfTable(col,
                    ["SKU", "Product", "Qty Sold", "Gross Revenue"],
                    report.BestSellingProducts.Select(p => new[] { p.Sku, p.ProductName, p.TotalQuantitySold.ToString("0.##"), $"{sym}{p.GrossSalesRevenue:N2}" }));

                col.Item().PaddingTop(14).PaddingBottom(8).Text("Sales by Cashier").Bold().FontSize(11);
                AddPdfTable(col,
                    ["Cashier", "Transactions", "Net Sales Revenue"],
                    report.SalesByCashier.Select(c => new[] { c.CashierName, c.CompletedTransactionCount.ToString(), $"{sym}{c.NetSalesRevenue:N2}" }));
            });
        return ToPdfResult(bytes, $"RJAM-Sales-Report-{report.FromUtc:yyyyMMdd}-{report.ToUtc:yyyyMMdd}.pdf");
    }

    public ReportFileResult ExportDeliveryExcel(DeliveryReportDto report)
    {
        using var wb = new XLWorkbook();
        AddSheet(wb, "Deliveries",
            ["Delivery #", "Status", "Driver", "Created", "Dispatched", "Received"],
            report.Deliveries.Select(d => new object?[] { d.DeliveryNumber, d.Status.ToString(), d.DriverOrCourierName, d.CreatedAt, d.DispatchedAt, d.ReceivedAt }));

        AddSheet(wb, "Discrepancies",
            ["Delivery #", "Product", "Dispatched", "Received", "Variance", "Reason", "Reconciliation"],
            report.DiscrepancyDeliveries.SelectMany(d => d.Items.Where(i => i.DiscrepancyQuantity != 0).Select(i =>
                new object?[] { d.DeliveryNumber, i.ProductName, i.DispatchedQuantity, i.ReceivedQuantity, i.DiscrepancyQuantity, i.DiscrepancyReason, i.ReconciliationStatus.ToString() })));

        return ToExcelResult(wb, $"RJAM-Delivery-Report-{report.FromUtc:yyyyMMdd}-{report.ToUtc:yyyyMMdd}.xlsx");
    }

    public ReportFileResult ExportDeliveryPdf(DeliveryReportDto report)
    {
        var bytes = BuildPdf("RJAM Bakery — Delivery Report",
            $"Period: {report.FromUtc:yyyy-MM-dd} to {report.ToUtc:yyyy-MM-dd} | Pending: {report.PendingCount}, Dispatched: {report.DispatchedCount}, Received: {report.ReceivedCount}",
            col =>
            {
                AddPdfTable(col,
                    ["Delivery #", "Status", "Driver", "Dispatched", "Received"],
                    report.Deliveries.Select(d => new[] { d.DeliveryNumber, d.Status.ToString(), d.DriverOrCourierName, d.DispatchedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-", d.ReceivedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-" }));
            });
        return ToPdfResult(bytes, $"RJAM-Delivery-Report-{report.FromUtc:yyyyMMdd}-{report.ToUtc:yyyyMMdd}.pdf");
    }

    public ReportFileResult ExportClosingExcel(ClosingReportDto report)
    {
        using var wb = new XLWorkbook();
        AddSheet(wb, "Daily Closing Items",
            ["Date", "Closing #", "Product", "Opening", "Deliveries", "Restocked Returns", "Sold", "Waste", "Expected", "Actual", "Variance", "Carryover"],
            report.Closings.SelectMany(c => c.Items.Select(i => new object?[]
            {
                c.OperatingDate.ToString("yyyy-MM-dd"), c.ClosingNumber, i.ProductName, i.OpeningStock,
                i.ConfirmedDeliveries, i.ApprovedRestockedReturns, i.TotalSold, i.RecordedWaste,
                i.ExpectedClosingStock, i.ActualClosingStock, i.VarianceQuantity, i.CarryoverToNextDay
            })));

        AddSheet(wb, "Waste & Returns",
            ["Ref #", "Location", "Category", "Item", "Quantity", "Outcome", "Reason"],
            report.WasteAndReturns.Select(w => new object?[]
            {
                w.ReferenceNumber, w.Location.ToString(), w.Category.ToString(),
                w.ProductName ?? w.RawMaterialName ?? "-", w.Quantity, w.InspectionOutcome.ToString(), w.Reason
            }));

        return ToExcelResult(wb, $"RJAM-Closing-Report-{report.FromDate:yyyyMMdd}-{report.ToDate:yyyyMMdd}.xlsx");
    }

    public ReportFileResult ExportClosingPdf(ClosingReportDto report)
    {
        var bytes = BuildPdf("RJAM Bakery — Daily Closing & Variance Report",
            $"Period: {report.FromDate:yyyy-MM-dd} to {report.ToDate:yyyy-MM-dd}",
            col =>
            {
                AddPdfTable(col,
                    ["Date", "Product", "Opening", "Inflows", "Sold", "Waste", "Expected", "Actual", "Variance", "Carryover"],
                    report.Closings.SelectMany(c => c.Items.Select(i => new[]
                    {
                        c.OperatingDate.ToString("yyyy-MM-dd"),
                        i.ProductName,
                        i.OpeningStock.ToString("0.##"),
                        (i.ConfirmedDeliveries + i.ApprovedRestockedReturns).ToString("0.##"),
                        i.TotalSold.ToString("0.##"),
                        i.RecordedWaste.ToString("0.##"),
                        i.ExpectedClosingStock.ToString("0.##"),
                        i.ActualClosingStock.ToString("0.##"),
                        i.VarianceQuantity.ToString("0.##"),
                        i.CarryoverToNextDay.ToString("0.##")
                    })));
            });
        return ToPdfResult(bytes, $"RJAM-Closing-Report-{report.FromDate:yyyyMMdd}-{report.ToDate:yyyyMMdd}.pdf");
    }

    private static void AddSheet(XLWorkbook wb, string name, IReadOnlyList<string> headers, IEnumerable<object?[]> rows)
    {
        var ws = wb.Worksheets.Add(name.Length > 31 ? name[..31] : name);
        for (int c = 0; c < headers.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2E1C16");
            cell.Style.Font.FontColor = XLColor.FromHtml("#F7F1EB");
        }

        int r = 2;
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Length; c++)
            {
                ws.Cell(r, c + 1).Value = row[c] switch
                {
                    null => Blank.Value,
                    decimal d => d,
                    int i => i,
                    long l => l,
                    DateTime dt => dt,
                    _ => SanitizeSpreadsheetText(row[c]!.ToString())
                };
            }
            r++;
        }

        ws.Columns().AdjustToContents();
    }

    /// <summary>
    /// Neutralizes spreadsheet formula injection (CSV/XLSX cells starting with =, +, -, @, tab, or CR).
    /// </summary>
    private static string SanitizeSpreadsheetText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? $"'{value}"
            : value;
    }

    private static byte[] BuildPdf(string title, string subtitle, Action<ColumnDescriptor> bodyBuilder)
    {
        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(x => x.FontSize(9).FontColor("#2E1C16"));

                page.Header().PaddingBottom(10).Column(col =>
                {
                    col.Item().Text(title).Bold().FontSize(15).FontColor("#2E1C16");
                    col.Item().Text(subtitle).FontSize(9).FontColor("#6F5B53");
                });

                page.Content().Column(bodyBuilder);

                page.Footer().AlignRight().Text(text =>
                {
                    text.Span("RJAM Bakery Management System — Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    private static void AddPdfTable(ColumnDescriptor parent, IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        parent.Item().Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                for (int i = 0; i < headers.Count; i++)
                {
                    cols.RelativeColumn();
                }
            });

            table.Header(h =>
            {
                foreach (var header in headers)
                {
                    h.Cell().Background("#2E1C16").Padding(5).Text(header).Bold().FontColor("#F7F1EB").FontSize(8);
                }
            });

            int idx = 0;
            foreach (var row in rows)
            {
                var bg = idx % 2 == 0 ? "#FFFFFF" : "#FBF7F3";
                foreach (var val in row)
                {
                    table.Cell().Background(bg).BorderBottom(1).BorderColor("#E6DBD1").Padding(4).Text(val).FontSize(8);
                }
                idx++;
            }
        });
    }

    private static ReportFileResult ToExcelResult(XLWorkbook wb, string fileName)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return new ReportFileResult(
            ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static ReportFileResult ToPdfResult(byte[] bytes, string fileName)
        => new(bytes, "application/pdf", fileName);
}

<<<<<<< HEAD
# RJAM Bakery Management and Inventory System

A centralized bakery operations and inventory platform connecting **Suppliers → Raw Materials → Production → Factory Finished Goods → Store Deliveries → Store Inventory → POS Sales → Daily Closing → Operational Reports**.

## Solution Architecture

- **`src/RjamBakery.Domain`** — Entities, enums, RBAC role/policy constants, domain exceptions, and dimensional unit conversion rules (`Mass`, `Volume`, `Count`, configurable `PackToBaseRatio`).
- **`src/RjamBakery.Application`** — Business services, FluentValidation validators, `IInventoryLedgerService`, `IAuditLogService`, `IDailyClosingService`, `IReportingService`, and `IDashboardService`.
- **`src/RjamBakery.Infrastructure`** — `AppDbContext` (EF Core 9 + `Pomelo.EntityFrameworkCore.MySql`), MySQL/MariaDB migrations, ASP.NET Core Identity + security stamp validation, `ClosedXML` Excel & `QuestPDF` PDF exporters, and reference defaults seeder (roles, units, starter categories, configurable system settings).
- **`src/RjamBakery.Web`** — Blazor Web App (`.NET 9` Interactive Server + Static SSR Auth), MudBlazor v9 UI using the Warm Espresso / Caramel / Cream design system, and REST API endpoints under `/api/*`.
- **`tests/RjamBakery.UnitTests`** & **`tests/RjamBakery.IntegrationTests`** — Unit, integration (isolated MariaDB/MySQL test DB), concurrency, RBAC, Part 9 E2E workflow, and bUnit component tests.

## Local Setup & Database

1. Configure your MySQL 8.0 or MariaDB 10.4+ connection string in `src/RjamBakery.Web/appsettings.Development.json` (default: `Server=127.0.0.1;Port=3306;Database=rjam_bakery_ms_db;User=root;Password=;`).
2. Apply migrations (also runs automatically on startup):
   ```powershell
   dotnet ef database update --project src/RjamBakery.Infrastructure --startup-project src/RjamBakery.Web
   ```
3. Launch the web application:
   ```powershell
   dotnet run --project src/RjamBakery.Web --launch-profile http
   ```
4. On first launch (when no user accounts exist), navigate to `/Account/Setup` (automatically redirected from `/Account/Login`) to create the initial **Owner / Administrator** account.
5. Configure bakery name, currency symbol (`₱` default), factory operating hours (`06:00`–`18:00` default), and closing target (`19:00` default) under `/settings`.

## Running Automated Tests

```powershell
dotnet test RjamBakery.slnx
```

Integration tests automatically manage an isolated `rjam_bakery_ms_test_db` database without modifying `rjam_bakery_ms_db`.
=======
# RJAM_Bakery_Inventory_System
RJAM is a commissioned web development project designed to provide a functional, user-friendly, and responsive digital solution based on the client's requirements.
>>>>>>> 029724978f829019d2b67a53ae1efd171031e0b3

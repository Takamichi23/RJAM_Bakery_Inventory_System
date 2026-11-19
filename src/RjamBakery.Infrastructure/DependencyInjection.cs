using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Entities;
using RjamBakery.Infrastructure.Identity;
using RjamBakery.Infrastructure.Persistence;
using RjamBakery.Infrastructure.Reporting;

namespace RjamBakery.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRjamInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), mySql =>
            {
                mySql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
            });
        });

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequiredUniqueChars = 4;
            options.Password.RequireDigit = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireNonAlphanumeric = false;
            options.User.RequireUniqueEmail = true;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.SignIn.RequireConfirmedAccount = false;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        services.Configure<SecurityStampValidatorOptions>(options =>
        {
            options.ValidationInterval = TimeSpan.FromSeconds(30);
        });

        services.AddHttpContextAccessor();
        services.AddScoped<ScopedUserAccessor>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IInventoryLedgerService, InventoryLedgerService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IRawMaterialService, RawMaterialService>();
        services.AddScoped<ISupplierService, SupplierService>();
        services.AddScoped<IMaterialReceiptService, MaterialReceiptService>();
        services.AddScoped<IRecipeService, RecipeService>();
        services.AddScoped<IProductionService, ProductionService>();
        services.AddScoped<IFactoryInventoryService, FactoryInventoryService>();
        services.AddScoped<IDeliveryService, DeliveryService>();
        services.AddScoped<IStoreInventoryService, StoreInventoryService>();
        services.AddScoped<IPosService, PosService>();
        services.AddScoped<IWasteAndReturnService, WasteAndReturnService>();
        services.AddScoped<IDailyClosingService, DailyClosingService>();
        services.AddScoped<IReportingService, ReportingService>();
        services.AddSingleton<IReportExportService, ReportExportService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<ISystemSettingService, SystemSettingService>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<DatabaseSeeder>();

        return services;
    }
}

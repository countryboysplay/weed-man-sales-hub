using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Application;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Auth;
using SalesHub.Application.Users;
using SalesHub.Domain;
using SalesHub.Infrastructure.Identity;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSalesHubInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is required (PostgreSQL).");

        services.AddDbContext<SalesHubDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
                   .UseSnakeCaseNamingConvention());
        services.AddScoped<IAppDb>(sp => sp.GetRequiredService<SalesHubDbContext>());

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                // Management assigns passwords (CLAUDE.md §3); length over
                // composition rules, hashing/lockout stay Identity's job.
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireDigit = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.User.RequireUniqueEmail = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<SalesHubDbContext>();

        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName));
        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<BusinessTime>();
        services.AddSingleton<ICorrelationAccessor, CorrelationContext>();

        services.AddScoped<IIdentityService, AspNetIdentityService>();
        services.AddScoped<IAuditWriter, EfAuditWriter>();
        services.AddScoped<IOutboxWriter, EfOutboxWriter>();
        services.AddScoped<IPublicIdGenerator, PostgresPublicIdGenerator>();
        services.AddScoped<IFileBlobStore, FileSystemBlobStore>();

        services.AddScoped<AuthenticationService>();
        services.AddScoped<IdleCapabilityService>();
        services.AddScoped<UserProvisioningService>();

        return services;
    }
}

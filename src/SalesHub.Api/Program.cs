using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SalesHub.Api.Auth;
using SalesHub.Api.Endpoints;
using SalesHub.Api.Hubs;
using SalesHub.Api.Middleware;
using SalesHub.Api.Startup;
using SalesHub.Application.Abstractions;
using SalesHub.Infrastructure;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Workers;
using SalesHub.Workers.Jobs;

var builder = WebApplication.CreateBuilder(args);

// ── Logging: structured JSON with correlation ids (CLAUDE.md §1) ─────────────
builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));

// ── Data + application services ──────────────────────────────────────────────
builder.Services.AddSalesHubInfrastructure(builder.Configuration);

// ── Data Protection: key ring outside the deployment directory (docs/04/08) ──
var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    Directory.CreateDirectory(keyRingPath);
    builder.Services.AddDataProtection()
        .SetApplicationName("WeedManSalesHub")
        .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
}

// ── Authentication: cookie + server-side session record (CLAUDE.md §3) ───────
builder.Services
    .AddAuthentication(AuthConstants.CookieScheme)
    .AddCookie(AuthConstants.CookieScheme, options =>
    {
        options.Cookie.Name = AuthConstants.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsProduction()
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        // API, not a website: never redirect to a login page.
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddSalesHubAuthorization();

// ── Antiforgery: header-token pattern for the JSON API (docs/04) ─────────────
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = ".SalesHub.Antiforgery";
    options.Cookie.HttpOnly = false; // double-submit partner cookie is not a secret bearer
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// ── Rate limiting (docs/04): tight window on credential endpoints ────────────
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ── ProblemDetails everywhere (docs/02) ──────────────────────────────────────
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions.TryAdd("correlationId", context.HttpContext.TraceIdentifier);
        context.ProblemDetails.Extensions.TryAdd("code",
            context.ProblemDetails.Status == StatusCodes.Status500InternalServerError
                ? "internalError"
                : context.ProblemDetails.Type?.Split('/').LastOrDefault() ?? "error");
    });

// ── Realtime + workers ───────────────────────────────────────────────────────
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRealtimePublisher, SignalRRealtimePublisher>();
builder.Services.AddSingleton<IScheduledJobHandler, IdleCapabilityStaleScanJob>();
builder.Services.AddSingleton<IScheduledJobHandler, IdempotencyKeyCleanupJob>();
builder.Services.AddSingleton<IScheduledJobHandler, ScheduledReactivationJob>();
builder.Services.AddSingleton<IScheduledJobHandler, AnnouncementMaintenanceJob>();
builder.Services.AddSingleton<IScheduledJobHandler, WorkMaintenanceJob>();
builder.Services.AddSingleton<IOutboxSideEffect, NotificationWebPushSideEffect>();
builder.Services.AddSingleton<OutboxDispatcher>();
builder.Services.AddSingleton<ScheduledJobRunner>();
if (builder.Configuration.GetValue("Workers:Enabled", true))
{
    // Tests disable the background loops and drive dispatch deterministically.
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduledJobRunner>());
}

// ── Health (docs/08): anonymous liveness; management readiness ───────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<SalesHubDbContext>("database")
    .AddCheck<OutboxHealthCheck>("outbox");
builder.Services.AddScoped<OutboxHealthCheck>();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi();
}

var app = builder.Build();

// ── Pipeline (assessment §8) ─────────────────────────────────────────────────
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsProduction())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseMiddleware<CorrelationMiddleware>();
app.UseSerilogRequestLogging();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<SessionGateMiddleware>();
app.UseAuthorization();
app.UseMiddleware<AntiforgeryMiddleware>();

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapUserEndpoints();
api.MapUserLifecycleEndpoints();
api.MapProfileEndpoints();
api.MapPasswordResetEndpoints();
api.MapNotificationEndpoints();
api.MapSalesEndpoints();
api.MapChatEndpoints();
api.MapAnnouncementEndpoints();
api.MapTaskEndpoints();
api.MapRecognitionEndpoints();
api.MapFormEndpoints();
api.MapResourceEndpoints();
api.MapGet("/auth/csrf", (IAntiforgery antiforgery, HttpContext http) =>
{
    var tokens = antiforgery.GetAndStoreTokens(http);
    return Results.Ok(new { token = tokens.RequestToken });
}).AllowAnonymous();

// Gate probes: the PWA checks these to route between the working app and the
// remediation/fresh-auth screens; the authorization test matrix drives them.
api.MapGet("/diagnostics/monitored-ping", () => Results.Ok(new { ok = true }))
    .RequireAuthorization(Policies.Employee, Policies.MonitoredWorkSession);
api.MapPost("/diagnostics/fresh-auth-ping", () => Results.Ok(new { ok = true }))
    .RequireAuthorization(Policies.Management, Policies.FreshAuthRequired);

app.MapHub<AppHub>("/hubs/app");
app.MapHub<SalesHub.Api.Hubs.ChatHub>("/hubs/chat");

// Liveness: anonymous and dependency-free (docs/08).
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false,
}).AllowAnonymous();

// Readiness: dependencies included; management only.
app.MapHealthChecks("/health/ready").RequireAuthorization(Policies.Management);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await StartupSeeder.SeedAsync(app.Services, app.Configuration);

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory-based tests.</summary>
public partial class Program;

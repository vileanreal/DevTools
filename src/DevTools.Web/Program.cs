using DevTools.Application;
using DevTools.Web.Data;
using DevTools.Web.Models;
using DevTools.Web.Security;
using DevTools.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

DotEnvLoader.LoadFirstExisting(
    Path.Combine(Directory.GetCurrentDirectory(), ".env"),
    Path.Combine(Directory.GetCurrentDirectory(), "src", "DevTools.Web", ".env"),
    Path.Combine(AppContext.BaseDirectory, ".env"));

var builder = WebApplication.CreateBuilder(args);
var configuredPathBase = builder.Configuration["Hosting:PathBase"]?.Trim();

var configurationDatabasePath = ResolveDatabasePath(
    builder.Configuration["DatabasePaths:ConfigurationDatabase"],
    "devtools.db",
    builder.Environment.ContentRootPath);
var logIndexDatabasePath = ResolveDatabasePath(
    builder.Configuration["DatabasePaths:LogIndexDatabase"],
    "devtools-log-index.db",
    builder.Environment.ContentRootPath);
var configurationConnectionString = new SqliteConnectionStringBuilder
{
    DataSource = configurationDatabasePath,
    ForeignKeys = true
}.ToString();
var logIndexConnectionString = new SqliteConnectionStringBuilder
{
    DataSource = logIndexDatabasePath,
    ForeignKeys = true
}.ToString();

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddOptions<ToolOptions>()
    .Bind(builder.Configuration.GetSection(ToolOptions.SectionName));
var loginSection = builder.Configuration.GetSection(LoginOptions.SectionName);
var useLogin = loginSection.GetValue(nameof(LoginOptions.UseLogin), true);
builder.Services.AddOptions<LoginOptions>()
    .Bind(loginSection)
    .Validate(options => options.HasValidHashes(),
        "Login UsernameSha256 and PasswordSha256 must be 64-character hexadecimal SHA-256 hashes when login is enabled.")
    .ValidateOnStart();
builder.Services.AddSingleton<ILoginCredentialVerifier, Sha256CredentialVerifier>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/login";
        options.Cookie.Name = "DevTools.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status403Forbidden;
            else context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    if (useLogin)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();
    }
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddDevToolsApplication();
builder.Services.AddDbContext<DevToolsDbContext>(options => options.UseSqlite(configurationConnectionString));
builder.Services.AddSingleton<SqliteRegexConnectionInterceptor>();
builder.Services.AddDbContext<LogIndexDbContext>((services, options) => options
    .UseSqlite(logIndexConnectionString)
    .AddInterceptors(services.GetRequiredService<SqliteRegexConnectionInterceptor>()));
builder.Services.AddScoped<ISystemLogService, SystemLogService>();
builder.Services.AddScoped<ILogIndexService, LogIndexService>();
builder.Services.AddSingleton<ILogIndexOperationCoordinator, LogIndexOperationCoordinator>();
builder.Services.AddSingleton<ILogIndexJobService, LogIndexJobService>();
builder.Services.AddSingleton<ILogIndexClearJobService, LogIndexClearJobService>();
builder.Services.AddScoped<ISqlDocService, SqlDocService>();
builder.Services.AddScoped<IDatabaseCompareService, DatabaseCompareService>();
builder.Services.AddScoped<ISystemServicesService, SystemServicesService>();
builder.Services.AddOptions<DeploymentOptions>().Bind(builder.Configuration.GetSection(DeploymentOptions.SectionName));
builder.Services.AddSingleton<IDeploymentPaths, DeploymentPaths>();
builder.Services.AddSingleton<DeploymentJobService>();
builder.Services.AddSingleton<IDeploymentJobService>(services => services.GetRequiredService<DeploymentJobService>());
builder.Services.AddSingleton<IDeploymentJobRegistry>(services => services.GetRequiredService<DeploymentJobService>());
builder.Services.AddScoped<IDeploymentService, DeploymentService>();
builder.Services.AddMemoryCache(options => options.SizeLimit = 5_000);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    DatabaseInitializer.Initialize(scope.ServiceProvider.GetRequiredService<DevToolsDbContext>());
    LogIndexDatabaseInitializer.Initialize(scope.ServiceProvider.GetRequiredService<LogIndexDbContext>());
    DeploymentService.MarkInterrupted(scope.ServiceProvider.GetRequiredService<DevToolsDbContext>());
}

// Establish the deployment base path before diagnostics, static files, routing, and authentication.
if (!string.IsNullOrWhiteSpace(configuredPathBase))
{
    app.UsePathBase(configuredPathBase);
    app.Use((context, next) =>
    {
        if (!context.Request.PathBase.HasValue)
            context.Request.PathBase = configuredPathBase;
        return next(context);
    });
}
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseStaticFiles();
app.UseRouting();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.UseStatusCodePagesWithReExecute("/Home/Error", "?statusCode={0}");

app.MapStaticAssets().AllowAnonymous();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

static string ResolveDatabasePath(string? configuredPath, string defaultFileName, string contentRootPath)
{
    var value = string.IsNullOrWhiteSpace(configuredPath) ? defaultFileName : configuredPath.Trim();
    value = Environment.ExpandEnvironmentVariables(value);
    var fullPath = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(contentRootPath, value));
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    return fullPath;
}

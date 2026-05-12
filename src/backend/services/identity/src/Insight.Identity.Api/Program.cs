using Insight.Identity.Api.Auth;
using Insight.Identity.Api.Configuration;
using Insight.Identity.Api.Contracts;
using Insight.Identity.Api.Endpoints;
using Insight.Identity.Domain.Services;
using Insight.Identity.Infrastructure.MariaDb;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Mirror the Rust service's snake_case env-var layout (IDENTITY__bind_addr,
// IDENTITY__database_url, IDENTITY__mariadb__url, ...). The double underscore
// becomes the configuration section delimiter.
builder.Configuration
    .AddYamlFile("appsettings.yaml", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "IDENTITY__");

builder.Host.UseSerilog((context, services, config) =>
{
    config
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("service", "identity")
        .WriteTo.Console(new CompactJsonFormatter());
});

builder.Services
    .AddOptions<AppOptions>()
    .Bind(builder.Configuration.GetSection(AppOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<MariaDbOptions>()
    .Bind(builder.Configuration.GetSection(MariaDbOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<MariaDbConnectionFactory>();
builder.Services.AddSingleton<PersonsRepository>();
builder.Services.AddSingleton<IPersonsReader>(sp => sp.GetRequiredService<PersonsRepository>());
builder.Services.AddSingleton<PersonLookupService>();

// Composite tenant resolver: header → JWT (stub) → config default.
builder.Services.AddSingleton<HeaderTenantContext>();
builder.Services.AddSingleton<JwtTenantContext>();
builder.Services.AddSingleton<ConfigTenantContext>();
builder.Services.AddSingleton<ITenantContext>(sp => new CompositeTenantContext(new ITenantContext[]
{
    sp.GetRequiredService<HeaderTenantContext>(),
    sp.GetRequiredService<JwtTenantContext>(),
    sp.GetRequiredService<ConfigTenantContext>(),
}));

builder.Services.AddRouting();

var bindAddr = builder.Configuration[$"{AppOptions.SectionName}:bind_addr"]
    ?? builder.Configuration["bind_addr"]
    ?? "0.0.0.0:8082";
builder.WebHost.UseUrls($"http://{bindAddr}");

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler(handler =>
{
    handler.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var ex = feature?.Error;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Insight.Identity.Api.UnhandledException");
#pragma warning disable CA1848 // single-call low-frequency error path; LoggerMessage adds noise here
        logger.LogError(ex, "Unhandled exception in {Path}", context.Request.Path);
#pragma warning restore CA1848

        var dbTarget = context.RequestServices.GetService<MariaDbConnectionFactory>()?.Target ?? "unknown";
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var problem = new ProblemResponse(
            Type: "urn:insight:error:internal",
            Title: "Internal Server Error",
            Status: StatusCodes.Status500InternalServerError,
            Detail: ex is null
                ? $"unknown error (db_target={dbTarget})"
                : $"{ex.GetType().Name}: {ex.Message} (db_target={dbTarget})");
        await context.Response.WriteAsJsonAsync(problem).ConfigureAwait(false);
    });
});
app.MapPersonsEndpoints();

await app.RunAsync().ConfigureAwait(false);

namespace Insight.Identity.Api
{
    /// <summary>Marker for the WebApplicationFactory in integration tests.</summary>
    public partial class Program;
}

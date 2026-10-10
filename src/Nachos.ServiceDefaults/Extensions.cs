using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Nachos.ServiceDefaults;

/// <summary>
/// Shared hosting defaults (telemetry, health, HTTP resilience) for every Nachos host,
/// following the standard .NET Aspire ServiceDefaults shape.
/// </summary>
public static class Extensions
{
    private const string LiveTag = "live";

    /// <summary>
    /// Adds OpenTelemetry (traces, metrics, logs), service discovery, standard HTTP resilience,
    /// and the default health checks.
    /// </summary>
    /// <remarks>
    /// An OTLP exporter is added when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set and an Azure Monitor
    /// exporter when <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is set; with neither, telemetry stays in-process.
    /// </remarks>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>Adds OpenTelemetry logging, metrics and tracing plus the environment-selected exporters.</summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(options =>
                    // Health probes would otherwise dominate trace volume.
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        var appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(appInsights))
        {
            telemetry.UseAzureMonitorExporter(options => options.ConnectionString = appInsights);
        }

        return builder;
    }

    /// <summary>Registers a <c>self</c> liveness check tagged <c>live</c>.</summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        return builder;
    }

    /// <summary>
    /// Maps <c>/health</c> (all checks), <c>/health/live</c> (checks tagged <c>live</c>)
    /// and <c>/health/ready</c> (all checks).
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(LiveTag),
        });
        app.MapHealthChecks("/health/ready");

        return app;
    }
}
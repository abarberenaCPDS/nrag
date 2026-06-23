using DotnetRag.Shared.Configuration;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DotnetRag.Rag.Observability;

public static class ObservabilityExtensions
{
    internal const string ServiceName = "dotnet-rag-server";
    internal const string ActivitySourceName = ServiceName;
    internal const string MeterName = ServiceName;

    /// <summary>
    /// Registers OpenTelemetry tracing + metrics pipelines.
    /// Prometheus scrape endpoint is always available at /metrics.
    /// OTLP export is added when TracingEnabled=true.
    /// </summary>
    public static IServiceCollection AddRagObservability(
        this IServiceCollection services,
        RagServerConfiguration config)
    {
        var resource = ResourceBuilder.CreateDefault()
            .AddService(ServiceName, serviceVersion: "1.0.0");

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .AddSource(ActivitySourceName)
                    .AddAspNetCoreInstrumentation(opts =>
                    {
                        // Exclude noisy health/metrics probes from traces
                        opts.Filter = ctx =>
                            !ctx.Request.Path.StartsWithSegments("/health") &&
                            !ctx.Request.Path.StartsWithSegments("/metrics");
                    })
                    .AddHttpClientInstrumentation();

                if (config.TracingEnabled && !string.IsNullOrWhiteSpace(config.OtlpHttpEndpoint))
                {
                    tracing.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(config.OtlpHttpEndpoint);
                        opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resource)
                    .AddMeter(MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddPrometheusExporter();

                if (config.TracingEnabled && !string.IsNullOrWhiteSpace(config.OtlpHttpEndpoint))
                {
                    metrics.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(config.OtlpHttpEndpoint);
                        opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    });
                }
            });

        return services;
    }
}

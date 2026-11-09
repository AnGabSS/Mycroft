using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mycroft.Infrastructure;
using Mycroft.Infrastructure.Persistence;

namespace Mycroft.Api.Endpoints;

public static class HealthEndpoints
{
    public const string OllamaHttpClient = "ollama-health";

    public static IServiceCollection AddMycroftHealthChecks(this IServiceCollection services)
    {
        services.AddHttpClient(OllamaHttpClient, c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddHealthChecks()
            .AddDbContextCheck<MycroftDbContext>("postgres")
            .AddCheck<OllamaHealthCheck>("ollama");
        return services;
    }

    public static IEndpointRouteBuilder MapMycroftHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteJsonAsync })
            .WithTags("Health");
        return app;
    }

    private static Task WriteJsonAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            durationMs = (long)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(e => e.Key, e => new
            {
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                error = e.Value.Exception?.Message,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, MycroftJson.Options));
    }
}

/// <summary>Healthy when Ollama answers and the configured model is pulled; Degraded when the model is missing.</summary>
public sealed class OllamaHealthCheck(IHttpClientFactory httpClientFactory, IOptions<MycroftOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        try
        {
            var client = httpClientFactory.CreateClient(HealthEndpoints.OllamaHttpClient);
            using var response = await client.GetAsync(new Uri(new Uri(o.OllamaEndpoint), "/api/tags"), cancellationToken);
            if (!response.IsSuccessStatusCode)
                return HealthCheckResult.Unhealthy($"Ollama returned HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var models = doc.RootElement.TryGetProperty("models", out var arr)
                ? arr.EnumerateArray().Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null).ToList()
                : [];

            var wanted = o.OllamaModel.Contains(':') ? o.OllamaModel : $"{o.OllamaModel}:latest";
            return models.Contains(wanted)
                ? HealthCheckResult.Healthy($"model {o.OllamaModel} available")
                : HealthCheckResult.Degraded($"Ollama is up but model '{o.OllamaModel}' is not pulled yet");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return HealthCheckResult.Unhealthy("Ollama unreachable", ex);
        }
    }
}

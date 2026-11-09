using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

namespace Mycroft.Api.Security;

/// <summary>
/// Optional shared-key authentication for <c>/api/*</c> (MYCROFT_API_KEY, header <c>X-Api-Key</c>).
/// Health, OpenAPI and the Scalar UI stay public so container healthchecks and docs keep working.
/// Without it, anyone who reaches the port can submit jobs and, through feedback, rewrite the global
/// merchant cache used by every client.
/// </summary>
public static class ApiKeyAuthentication
{
    public const string HeaderName = "X-Api-Key";
    public const string SchemeId = "ApiKey";

    public static string? GetConfiguredKey(IConfiguration configuration) =>
        configuration["MYCROFT_API_KEY"] is { Length: > 0 } key ? key : null;

    public static IApplicationBuilder UseMycroftApiKey(this WebApplication app)
    {
        var key = GetConfiguredKey(app.Configuration);
        if (key is null)
        {
            app.Logger.LogWarning("MYCROFT_API_KEY is not set: /api endpoints are unauthenticated. Set it before exposing the port beyond localhost.");
            return app;
        }

        // Compare hashes so both sides have the same length and the comparison is constant-time.
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                var provided = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers[HeaderName].ToString()));
                if (!CryptographicOperations.FixedTimeEquals(provided, expected))
                {
                    await Results.Problem(
                        title: "Unauthorized",
                        detail: $"Missing or invalid {HeaderName} header.",
                        statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
                    return;
                }
            }

            await next(context);
        });
    }

    /// <summary>Declares the key in the OpenAPI document so Scalar shows an auth field.</summary>
    public static OpenApiOptions AddApiKeySecurityScheme(this OpenApiOptions options, bool enabled)
    {
        if (!enabled) return options;
        return options.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Components ??= new OpenApiComponents();
            doc.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = HeaderName,
                Description = "Value of MYCROFT_API_KEY.",
            };
            doc.SecurityRequirements.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = SchemeId } }] = [],
            });
            return Task.CompletedTask;
        });
    }
}

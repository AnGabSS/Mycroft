using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Infrastructure.Inference;
using Mycroft.Infrastructure.Parsing;
using Mycroft.Infrastructure.Persistence;
using Mycroft.Infrastructure.Processing;
using Mycroft.Infrastructure.Webhooks;
using OllamaSharp;

namespace Mycroft.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMycroftInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<MycroftDbContext>(o => o.UseNpgsql(connectionString, npg => npg.EnableRetryOnFailure(3)));
        return services.AddMycroftCore();
    }

    /// <summary>Everything except the database provider (lets tests plug a different provider).</summary>
    public static IServiceCollection AddMycroftCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        // Parsing
        services.AddSingleton<IStatementParser, CsvStatementParser>();
        services.AddSingleton<IStatementParser, OfxStatementParser>();
        services.AddSingleton<IStatementParser>(new CnabStatementParser(SourceFormat.Cnab240));
        services.AddSingleton<IStatementParser>(new CnabStatementParser(SourceFormat.Cnab400));
        services.AddSingleton<IStatementParser, PdfStatementParser>();
        services.AddSingleton<IStatementParserFactory, StatementParserFactory>();

        // Inference
        services.AddSingleton<IMerchantNormalizer, MerchantNormalizer>();
        services.AddSingleton(sp => new RegexFallbackExtractor(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<SlmConcurrencyGate>();
        services.TryAddSingleton<IChatClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<MycroftOptions>>().Value;
            var http = new HttpClient { BaseAddress = new Uri(o.OllamaEndpoint), Timeout = Timeout.InfiniteTimeSpan };
            return new OllamaApiClient(http, o.OllamaModel);
        });
        services.AddScoped<ITransactionInferenceService, OllamaTransactionInferenceService>();

        // Webhooks
        services.AddHttpClient(HmacWebhookDispatcher.HttpClientName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd("Mycroft-Webhook/1.0"));
        services.AddSingleton<IWebhookDispatcher, HmacWebhookDispatcher>();

        // Application services
        services.AddScoped(sp => new ReconciliationProcessor(
            sp.GetRequiredService<MycroftDbContext>(),
            sp.GetRequiredService<IStatementParserFactory>(),
            sp.GetRequiredService<IMerchantNormalizer>(),
            sp.GetRequiredService<ITransactionInferenceService>(),
            sp.GetRequiredService<IOptions<MycroftOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ReconciliationProcessor>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new WebhookDeliveryService(
            sp.GetRequiredService<MycroftDbContext>(),
            sp.GetRequiredService<IWebhookDispatcher>(),
            sp.GetRequiredService<IOptions<MycroftOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookDeliveryService>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new JobIngestionService(
            sp.GetRequiredService<MycroftDbContext>(),
            sp.GetRequiredService<IReconciliationJobQueue>(),
            sp.GetRequiredService<IMerchantNormalizer>(),
            sp.GetRequiredService<IOptions<MycroftOptions>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new FeedbackService(sp.GetRequiredService<MycroftDbContext>(), sp.GetRequiredService<TimeProvider>()));

        return services;
    }
}

using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mycroft.Infrastructure;
using Mycroft.Infrastructure.Inference;

namespace Mycroft.Api.Workers;

/// <summary>
/// Loads the model into memory right after startup with a one-token request. On CPU-only hosts the cold
/// load takes minutes; paying it here keeps it out of the first real job's SLM timeout. The request holds the
/// SLM gate, so jobs that arrive meanwhile wait for it instead of competing for the same CPU.
/// Combined with OLLAMA_KEEP_ALIVE the model then stays loaded between jobs.
/// </summary>
public sealed class SlmWarmupWorker(
    IChatClient chatClient,
    SlmConcurrencyGate gate,
    IOptions<MycroftOptions> options,
    ILogger<SlmWarmupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.SlmWarmup) return;

        for (var attempt = 0; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using (await gate.AcquireAsync(stoppingToken))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(options.Value.SlmRequestTimeout);
                    await chatClient.GetResponseAsync(
                        [new ChatMessage(ChatRole.User, "ok")],
                        new ChatOptions { MaxOutputTokens = 1, Temperature = 0f },
                        timeout.Token);
                }

                logger.LogInformation("SLM model {Model} warmed up in {Elapsed:0.0}s", options.Value.OllamaModel, sw.Elapsed.TotalSeconds);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (attempt < RetryDelays.Length)
            {
                logger.LogWarning("SLM warm-up attempt {Attempt} failed: {Message}", attempt + 1, ex.Message);
                await Task.Delay(RetryDelays[attempt], stoppingToken);
            }
            catch (Exception ex)
            {
                // Not fatal: the first job will load the model itself.
                logger.LogWarning(ex, "SLM warm-up gave up; the first job will pay the model load time");
                return;
            }
        }
    }
}

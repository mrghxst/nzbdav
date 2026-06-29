using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(List<MultiConnectionNntpClient> providers) : NntpClient
{
    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.StatAsync(segmentId, cancellationToken), cancellationToken, useStreamingPriority: false);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.HeadAsync(segmentId, cancellationToken), cancellationToken, useStreamingPriority: false);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedBodyAsync(segmentId, cancellationToken), cancellationToken, useStreamingPriority: true);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedArticleAsync(segmentId, cancellationToken), cancellationToken, useStreamingPriority: true);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken, useStreamingPriority: false);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        return RunDecodeWithReadyCallback(
            readyCallback => RunFromPoolWithBackup(
                x => x.DecodedBodyAsync(segmentId, readyCallback, cancellationToken),
                cancellationToken,
                useStreamingPriority: true),
            UsenetResponseType.ArticleRetrievedBodyFollows,
            onConnectionReadyAgain);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        return RunDecodeWithReadyCallback(
            readyCallback => RunFromPoolWithBackup(
                x => x.DecodedArticleAsync(segmentId, readyCallback, cancellationToken),
                cancellationToken,
                useStreamingPriority: true),
            UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
            onConnectionReadyAgain);
    }

    // Shared wrapper for the two streaming overloads above: forward a "connection ready again"
    // signal to the caller only on a successful retrieval, and guarantee the caller is told
    // "not retrieved" exactly once on failure or a non-success response.
    private static async Task<T> RunDecodeWithReadyCallback<T>
    (
        Func<Action<ArticleBodyResult>, Task<T>> run,
        UsenetResponseType successResponseType,
        Action<ArticleBodyResult>? onConnectionReadyAgain
    ) where T : UsenetResponse
    {
        T result;
        try
        {
            result = await run(OnConnectionReadyAgain).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != successResponseType)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken,
        bool useStreamingPriority = true
    ) where T : UsenetResponse
    {
        ExceptionDispatchInfo? lastException = null;
        T? lastNoArticleResult = null;
        var lastOutcomeWasException = false;

        // Backbones that have authoritatively reported the article missing during
        // this single request. Providers sharing one of these labels are skipped,
        // since they share the same upstream storage and would only 430 again.
        var missingBackbones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedProviders = GetOrderedProviders(useStreamingPriority);

        foreach (var provider in orderedProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var backbone = provider.Backbone?.Trim() ?? "";
            var isGrouped = backbone.Length > 0;

            // Skip providers on a backbone a sibling already reported missing.
            if (isGrouped && missingBackbones.Contains(backbone))
            {
                Log.Debug(
                    "Skipping provider `{Provider}` on backbone `{Backbone}` — " +
                    "a sibling provider already reported the article missing.",
                    provider.ProviderName, backbone);
                continue;
            }

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            try
            {
                var result = await task.Invoke(provider).ConfigureAwait(false);

                // if no article with that message-id is found, try again with the next provider.
                // Only a definitive 430 marks the backbone missing — never a connection error.
                if (result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                {
                    lastNoArticleResult = result;
                    lastOutcomeWasException = false;
                    if (isGrouped) missingBackbones.Add(backbone);
                    continue;
                }

                return result;
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                lastException = ExceptionDispatchInfo.Capture(e);
                lastOutcomeWasException = true;
            }
        }

        // Whichever terminal outcome occurred on the last attempted provider wins,
        // matching the original fallback precedence (a later connection error beats
        // an earlier 430, and a later 430 beats an earlier error).
        if (lastOutcomeWasException) lastException!.Throw();
        if (lastNoArticleResult is not null) return lastNoArticleResult;
        throw new Exception("There are no usenet providers configured.");
    }

    private List<MultiConnectionNntpClient> GetOrderedProviders(bool useStreamingPriority)
    {
        var enabled = providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .ToList();

        // Backups are always last, ordered by type (BackupAndStats before BackupOnly) then priority
        var backups = enabled
            .Where(x => x.ProviderType != ProviderType.Pooled)
            .OrderBy(x => x.ProviderType)
            .ThenBy(x => x.Priority)
            .ThenByDescending(x => x.AvailableConnections)
            .ToList();

        List<MultiConnectionNntpClient> pooled;
        if (useStreamingPriority)
        {
            // Streaming: fill up priority-0 providers first, only overflow to higher
            // priority tiers when all lower-priority providers are fully saturated.
            // Among providers at the same priority, prefer the one with most free connections.
            pooled = enabled
                .Where(x => x.ProviderType == ProviderType.Pooled)
                .OrderBy(x => x.AvailableConnections > 0 ? 0 : 1)
                .ThenBy(x => x.Priority)
                .ThenByDescending(x => x.AvailableConnections)
                .ToList();
        }
        else
        {
            // Health checks / imports: spread load across all pooled providers equally,
            // routed to whichever has the most available connections right now.
            pooled = enabled
                .Where(x => x.ProviderType == ProviderType.Pooled)
                .OrderByDescending(x => x.AvailableConnections)
                .ToList();
        }

        var ordered = new List<MultiConnectionNntpClient>(pooled.Count + backups.Count);
        ordered.AddRange(pooled);
        ordered.AddRange(backups);

        var healthy = ordered.Where(x => !x.IsTripped).ToList();

        // Always return at least one provider so cooldown probes can fire.
        return healthy.Count > 0 ? healthy : ordered;
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}
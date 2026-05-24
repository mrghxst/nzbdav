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

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedBodyResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken,
                useStreamingPriority: true
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedArticleResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken,
                useStreamingPriority: true
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
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
        var orderedProviders = GetOrderedProviders(useStreamingPriority);
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            try
            {
                var result = await task.Invoke(provider).ConfigureAwait(false);

                // if no article with that message-id is found, try again with the next provider.
                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                    continue;

                return result;
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private List<MultiConnectionNntpClient> GetOrderedProviders(bool useStreamingPriority)
    {
        var enabled = providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .ToList();

        List<MultiConnectionNntpClient> ordered;
        if (useStreamingPriority)
        {
            // Streaming: respect user's priority preferences within pooled providers
            ordered = enabled
                .OrderBy(x => x.ProviderType)
                .ThenBy(x => x.Priority)
                .ThenByDescending(x => x.AvailableConnections)
                .ToList();
        }
        else
        {
            // Health checks / imports: spread load across all pooled providers equally,
            // then fall back to backup providers in priority order
            var pooled = enabled
                .Where(x => x.ProviderType == ProviderType.Pooled)
                .OrderByDescending(x => x.AvailableConnections)
                .ToList();
            var backups = enabled
                .Where(x => x.ProviderType != ProviderType.Pooled)
                .OrderBy(x => x.ProviderType)
                .ThenBy(x => x.Priority)
                .ThenByDescending(x => x.AvailableConnections)
                .ToList();
            ordered = [..pooled, ..backups];
        }

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
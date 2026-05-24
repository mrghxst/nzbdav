using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.TestUsenetConnection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Models.Nzb;
using UsenetSharp.Models;

namespace NzbWebDAV.Api.Controllers.TestUsenetSpeed;

[ApiController]
[Route("api/test-usenet-speed")]
public class TestUsenetSpeedController() : BaseApiController
{
    private static readonly HttpClient HttpClient = new();
    private const string TestNzbUrl = "https://sabnzbd.org/tests/test_download_100MB.nzb";

    private async Task<TestUsenetSpeedResponse> TestUsenetSpeed(TestUsenetConnectionRequest request)
    {
        try
        {
            // Download the NZB
            await using var nzbStream = await HttpClient.GetStreamAsync(TestNzbUrl, HttpContext.RequestAborted).ConfigureAwait(false);
            var nzb = await NzbDocument.LoadAsync(nzbStream).ConfigureAwait(false);
            
            // Extract segments to download (skip PAR2 for simplicity, just grab everything)
            var segments = nzb.Files.SelectMany(f => f.Segments).ToList();
            if (segments.Count == 0)
                return new TestUsenetSpeedResponse { Success = false };

            var connectionDetails = request.ToConnectionDetails();
            var maxConnections = Math.Min(10, Math.Max(1, connectionDetails.MaxConnections));

            // Create a temporary connection pool
            await using var pool = new ConnectionPool<INntpClient>(
                maxConnections,
                ct => UsenetStreamingClient.CreateNewConnection(connectionDetails, ct)
            );

            long totalBytesDownloaded = 0;
            var sw = Stopwatch.StartNew();

            await Parallel.ForEachAsync(segments, new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConnections,
                CancellationToken = HttpContext.RequestAborted
            }, async (segment, ct) =>
            {
                ConnectionLock<INntpClient>? lockObj = null;
                try
                {
                    lockObj = await pool.GetConnectionLockAsync(SemaphorePriority.High, ct).ConfigureAwait(false);
                    var segmentId = new SegmentId(segment.MessageId);
                    
                    var response = await lockObj.Connection.DecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
                    if (response.Success && response.Stream != null)
                    {
                        await using var stream = response.Stream;
                        var buffer = new byte[81920]; // 80KB buffer
                        int read;
                        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            Interlocked.Add(ref totalBytesDownloaded, read);
                        }
                    }
                }
                catch
                {
                    // If an error happens on a connection, we replace it in the pool
                    lockObj?.Replace();
                }
                finally
                {
                    lockObj?.Dispose();
                }
            });

            sw.Stop();

            if (totalBytesDownloaded == 0)
            {
                return new TestUsenetSpeedResponse { Success = false };
            }

            var totalMegabytes = totalBytesDownloaded / 1024.0 / 1024.0;
            var speedMBps = totalMegabytes / sw.Elapsed.TotalSeconds;
            var perConnectionSpeed = speedMBps / maxConnections;

            return new TestUsenetSpeedResponse 
            { 
                Success = true, 
                SpeedMBps = Math.Round(speedMBps, 2),
                SpeedMBpsPerConnection = Math.Round(perConnectionSpeed, 2),
                ConnectionsUsed = maxConnections
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new TestUsenetSpeedResponse { Success = false };
        }
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestUsenetConnectionRequest(HttpContext);
        var response = await TestUsenetSpeed(request).ConfigureAwait(false);
        return Ok(response);
    }
}

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace SmartLibrary.Api.Services;

// Incremental, self-healing embedding indexer for local Development.
// - Starts with the API; scans all visible books, including those imported by SQL.
// - Wakes immediately after a book is created/edited via the API.
// - Retries after LM Studio is started/restarted, without blocking book saves.
// - Never exposes Aiven or LM Studio credentials to WPF.
// - One small batch at a time: avoids saturating the local inference model.
public sealed class BookEmbeddingAutoIndexer : BackgroundService
{
    private const int BatchSize = 4;
    private static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NextBatchDelay = TimeSpan.FromSeconds(2);

    private readonly IConfiguration _config;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<BookEmbeddingAutoIndexer> _logger;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    private string _lastProblem = "";

    public BookEmbeddingAutoIndexer(IConfiguration config, IHostEnvironment environment,
        ILogger<BookEmbeddingAutoIndexer> logger)
    {
        _config = config;
        _environment = environment;
        _logger = logger;
    }

    public void RequestScan() => _wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The current WPF/API system is intentionally localhost/Development-only.
        if (!_environment.IsDevelopment()) return;
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            bool more = false;
            try
            {
                var result = await SemanticBookEndpoints.IndexChangedAsync(_config, BatchSize, stoppingToken);
                switch (result.Status)
                {
                    case "not_configured":
                        WarnOnce("not_configured", "Auto embedding: cấu hình LMStudio:EmbeddingModel và BaseUrl để bật lập chỉ mục.");
                        break;
                    case "busy":
                        break; // Admin may have started a maintenance reindex.
                    case "embedding_unavailable":
                        WarnOnce("embedding_unavailable", "Auto embedding: LM Studio chưa phản hồi vector 1024 chiều; sẽ thử lại sau.");
                        break;
                    default:
                        _lastProblem = "";
                        if (result.Indexed > 0)
                            _logger.LogInformation("Auto embedding: indexed {Count}, remaining {Pending}, scanned {Scanned}.",
                                result.Indexed, result.Pending, result.Scanned);
                        more = result.Pending > 0 && result.Indexed > 0;
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                WarnOnce(ex.GetType().Name, "Auto embedding: không thể đọc/ghi metadata; kiểm tra Aiven và migration. Loại lỗi: " + ex.GetType().Name);
            }

            try
            {
                if (more)
                {
                    // Finish the initial 38-book backfill in consecutive short batches.
                    await Task.Delay(NextBatchDelay, stoppingToken);
                    continue;
                }
                // Either wake on a book save, or rescan periodically for SQL imports,
                // edits to author/category/metadata tables and LM Studio recovery.
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(NormalInterval);
                try { await _wake.Reader.WaitToReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                while (_wake.Reader.TryRead(out _)) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private void WarnOnce(string key, string message)
    {
        if (_lastProblem == key) return;
        _lastProblem = key;
        _logger.LogWarning("{Message}", message);
    }
}

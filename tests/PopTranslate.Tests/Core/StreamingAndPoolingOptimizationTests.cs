using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using PopTranslate.Core.Networking;
using PopTranslate.Core.Translation;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class StreamingAndPoolingOptimizationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DpapiSecretProtector _protector = new();
    private readonly JsonSettingsStore _settingsStore;
    private readonly ProxyHttpClientFactory _proxyFactory;

    public StreamingAndPoolingOptimizationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PopTranslateStreamPoolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsStore = new JsonSettingsStore(_protector, Path.Combine(_tempDir, "settings.json"));
        _proxyFactory = new ProxyHttpClientFactory(_settingsStore, _protector);
    }

    public void Dispose()
    {
        _proxyFactory.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    [Fact]
    public async Task ExecuteStreamingChatAsync_MultipleChunks_DataPrefixVariants_AndMultiLineSse_StreamsAccurately()
    {
        var sseBuilder = new StringBuilder();
        sseBuilder.Append(": keep-alive comment\n\n");
        sseBuilder.Append("event: message\n");
        sseBuilder.Append("data:\n");
        sseBuilder.Append("data:   \n");
        // Single space after data:
        sseBuilder.Append("data: {\"choices\":[{\"delta\":{\"content\":\"One \"}}]}\n\n");
        // No space after data:
        sseBuilder.Append("data:{\"choices\":[{\"delta\":{\"content\":\"Two \"}}]}\n\n");
        // Uppercase DATA: prefix
        sseBuilder.Append("DATA: {\"choices\":[{\"delta\":{\"content\":\"Three \"}}]}\n\n");
        // Two spaces after data: (strips only one optional space per SSE spec; JSON still valid)
        sseBuilder.Append("data:  {\"choices\":[{\"delta\":{\"content\":\"Four \"}}]}\n\n");
        // Multi-line SSE data: frame split across multiple lines
        sseBuilder.Append("data: {\"choices\":[{\"delta\":\n");
        sseBuilder.Append("data: {\"content\":\"Five \"}}]}\n\n");
        // Several additional chunks exercising repeated CTS timer reuse across many lines
        for (var i = 6; i <= 12; i++)
        {
            sseBuilder.Append($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{i} \"}}}}]}}\n\n");
        }

        sseBuilder.Append("data: [DONE]   \n\n");

        var factory = CreateStubFactory((_, _) => Task.FromResult(CreateSseResponse(sseBuilder.ToString())));

        var text = new StringBuilder();
        var chunks = new List<ReasoningStreamChunk>();
        await foreach (var item in OpenAiStreamHelper.ExecuteStreamingChatAsync(
            factory,
            _protector,
            CreateTestConfig(),
            new Uri("https://mock-stream.local/v1/chat/completions"),
            "System prompt",
            "User input",
            LanguageCode.English))
        {
            chunks.Add(item);
            if (!string.IsNullOrEmpty(item.Chunk.DeltaText))
            {
                text.Append(item.Chunk.DeltaText);
            }
        }

        Assert.NotEmpty(chunks);
        Assert.True(chunks[^1].Chunk.IsComplete);
        Assert.Null(chunks[^1].Chunk.ErrorMessage);
        Assert.Equal("One Two Three Four Five 6 7 8 9 10 11 12 ", text.ToString());
    }

    [Fact]
    public async Task ExecuteStreamingChatAsync_ReasoningToNormalIdleTimeoutSwitching_RespectsBothBudgets()
    {
        var normalIdle = TimeSpan.FromMilliseconds(200);
        var reasoningIdle = TimeSpan.FromMilliseconds(1500);

        var stream = new AsyncChunkStream();
        var producerCts = new CancellationTokenSource();
        var producerTask = Task.Run(async () =>
        {
            try
            {
                // Enter reasoning phase (switches idle timeout from normalIdle 200ms -> reasoningIdle 1500ms)
                stream.Enqueue("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking...\"}}]}\n\n");

                // Pause 420ms: exceeds normalIdle (200ms) but well within reasoningIdle (1500ms)
                await Task.Delay(420, producerCts.Token).ConfigureAwait(false);

                // Emit visible content (switches idle timeout back to normalIdle 200ms)
                stream.Enqueue("data: {\"choices\":[{\"delta\":{\"content\":\"Answer\"}}]}\n\n");

                // Short pause within normalIdle
                await Task.Delay(40, producerCts.Token).ConfigureAwait(false);
                stream.Enqueue("data: {\"choices\":[{\"delta\":{\"content\":\" part2\"}}]}\n\n");

                // Stall 800ms: exceeds normalIdle (200ms) while still below reasoningIdle (1500ms).
                // Proves the reused CTS reverted from reasoningIdle back to normalIdle.
                await Task.Delay(800, producerCts.Token).ConfigureAwait(false);
                stream.Enqueue("data: {\"choices\":[{\"delta\":{\"content\":\" SHOULD_NOT_ARRIVE\"}}]}\n\n");
                stream.Enqueue("data: [DONE]\n\n");
                stream.Complete();
            }
            catch (OperationCanceledException)
            {
                // Expected on cleanup.
            }
        });

        var factory = CreateStubFactory((_, _) => Task.FromResult(CreateStreamResponse(stream)));

        var sawReasoningHeartbeat = false;
        var visibleText = new StringBuilder();
        string? errorMessage = null;

        try
        {
            await foreach (var item in OpenAiStreamHelper.ExecuteStreamingChatAsync(
                factory,
                _protector,
                CreateTestConfig(),
                new Uri("https://mock-stream.local/v1/chat/completions"),
                "System prompt",
                "User input",
                LanguageCode.English,
                normalIdleTimeoutOverride: normalIdle,
                reasoningIdleTimeoutOverride: reasoningIdle))
            {
                if (item.IsReasoning)
                {
                    sawReasoningHeartbeat = true;
                }

                if (!string.IsNullOrEmpty(item.Chunk.DeltaText))
                {
                    visibleText.Append(item.Chunk.DeltaText);
                }

                if (!string.IsNullOrEmpty(item.Chunk.ErrorMessage))
                {
                    errorMessage = item.Chunk.ErrorMessage;
                }
            }
        }
        finally
        {
            producerCts.Cancel();
            await producerTask;
            producerCts.Dispose();
        }

        Assert.True(sawReasoningHeartbeat);
        Assert.Equal("Answer part2", visibleText.ToString());
        Assert.NotNull(errorMessage);
        Assert.Contains("timed out", errorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteStreamingChatAsync_TimeoutTriggersAfterReusingCtsAcrossManyLines()
    {
        var normalIdle = TimeSpan.FromMilliseconds(160);
        var reasoningIdle = TimeSpan.FromMilliseconds(1000);

        var stream = new AsyncChunkStream();
        var producerCts = new CancellationTokenSource();
        var producerTask = Task.Run(async () =>
        {
            try
            {
                for (var i = 1; i <= 15; i++)
                {
                    await Task.Delay(15, producerCts.Token).ConfigureAwait(false);
                    stream.Enqueue($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"c{i},\"}}}}]}}\n\n");
                }

                // Stall beyond normalIdle after 15+ arm/disarm cycles on the reused CTS
                await Task.Delay(1200, producerCts.Token).ConfigureAwait(false);
                stream.Enqueue("data: [DONE]\n\n");
                stream.Complete();
            }
            catch (OperationCanceledException)
            {
                // Expected on cleanup.
            }
        });

        var factory = CreateStubFactory((_, _) => Task.FromResult(CreateStreamResponse(stream)));

        var visibleText = new StringBuilder();
        string? errorMessage = null;

        try
        {
            await foreach (var item in OpenAiStreamHelper.ExecuteStreamingChatAsync(
                factory,
                _protector,
                CreateTestConfig(),
                new Uri("https://mock-stream.local/v1/chat/completions"),
                "System prompt",
                "User input",
                LanguageCode.English,
                normalIdleTimeoutOverride: normalIdle,
                reasoningIdleTimeoutOverride: reasoningIdle))
            {
                if (!string.IsNullOrEmpty(item.Chunk.DeltaText))
                {
                    visibleText.Append(item.Chunk.DeltaText);
                }

                if (!string.IsNullOrEmpty(item.Chunk.ErrorMessage))
                {
                    errorMessage = item.Chunk.ErrorMessage;
                }
            }
        }
        finally
        {
            producerCts.Cancel();
            await producerTask;
            producerCts.Dispose();
        }

        var expectedPrefix = string.Concat(Enumerable.Range(1, 15).Select(i => $"c{i},"));
        Assert.Equal(expectedPrefix, visibleText.ToString());
        Assert.NotNull(errorMessage);
        Assert.Contains("timed out", errorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteStreamingChatAsync_ExternalCancellationAfterReusingCts_ThrowsOperationCanceledException()
    {
        var waitingForNextRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyToCancel = 0;

        var stream = new AsyncChunkStream(() =>
        {
            if (Volatile.Read(ref readyToCancel) == 1)
            {
                waitingForNextRead.TrySetResult();
            }
        });

        stream.Enqueue("data: {\"choices\":[{\"delta\":{\"content\":\"chunk1\"}}]}\n\n");

        var factory = CreateStubFactory((_, _) => Task.FromResult(CreateStreamResponse(stream)));
        using var externalCts = new CancellationTokenSource();

        var cancelTask = Task.Run(async () =>
        {
            await waitingForNextRead.Task.ConfigureAwait(false);
            await Task.Delay(25).ConfigureAwait(false);
            externalCts.Cancel();
        });

        var received = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in OpenAiStreamHelper.ExecuteStreamingChatAsync(
                factory,
                _protector,
                CreateTestConfig(),
                new Uri("https://mock-stream.local/v1/chat/completions"),
                "System prompt",
                "User input",
                LanguageCode.English,
                cancellationToken: externalCts.Token,
                normalIdleTimeoutOverride: TimeSpan.FromSeconds(10),
                reasoningIdleTimeoutOverride: TimeSpan.FromSeconds(20)))
            {
                if (!string.IsNullOrEmpty(item.Chunk.DeltaText))
                {
                    received.Add(item.Chunk.DeltaText);
                    if (received.Count == 1)
                    {
                        stream.Enqueue("data: {\"choices\":[{\"delta\":{\"content\":\"chunk2\"}}]}\n\n");
                    }
                    else if (received.Count == 2)
                    {
                        stream.Enqueue("data: {\"choices\":[{\"delta\":{\"content\":\"chunk3\"}}]}\n\n");
                    }
                    else if (received.Count == 3)
                    {
                        Volatile.Write(ref readyToCancel, 1);
                    }
                }
            }
        });

        await cancelTask;
        Assert.Equal(["chunk1", "chunk2", "chunk3"], received);
    }

    [Fact]
    public async Task ExecuteStreamingChatAsync_FallbackJsonAndInlineApiError_PreservedIdentically()
    {
        // 1. Leading whitespace + JSON body served as text/event-stream falls back to non-streaming JSON parsing
        const string fallbackJson = "   {\n  \"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"Fallback OK\"}}]\n}\n";
        var fallbackFactory = CreateStubFactory((_, _) => Task.FromResult(CreateSseResponse(fallbackJson)));

        var fallbackText = new StringBuilder();
        string? fallbackError = null;
        await foreach (var item in OpenAiStreamHelper.ExecuteStreamingChatAsync(
            fallbackFactory,
            _protector,
            CreateTestConfig(),
            new Uri("https://mock-stream.local/v1/chat/completions"),
            "System prompt",
            "User input",
            LanguageCode.English))
        {
            if (!string.IsNullOrEmpty(item.Chunk.DeltaText))
            {
                fallbackText.Append(item.Chunk.DeltaText);
            }

            if (!string.IsNullOrEmpty(item.Chunk.ErrorMessage))
            {
                fallbackError = item.Chunk.ErrorMessage;
            }
        }

        Assert.Null(fallbackError);
        Assert.Equal("Fallback OK", fallbackText.ToString());

        // 2. Inline API error frame inside SSE stream terminates with error message
        const string errorSse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n" +
            "data: {\"error\":{\"message\":\"Upstream overloaded\"}}\n\n";
        var errorFactory = CreateStubFactory((_, _) => Task.FromResult(CreateSseResponse(errorSse)));

        string? apiError = null;
        await foreach (var item in OpenAiStreamHelper.ExecuteStreamingChatAsync(
            errorFactory,
            _protector,
            CreateTestConfig(),
            new Uri("https://mock-stream.local/v1/chat/completions"),
            "System prompt",
            "User input",
            LanguageCode.English))
        {
            if (!string.IsNullOrEmpty(item.Chunk.ErrorMessage))
            {
                apiError = item.Chunk.ErrorMessage;
            }
        }

        Assert.Equal("Upstream overloaded", apiError);
    }

    [Theory]
    [InlineData(ProxyMode.System)]
    [InlineData(ProxyMode.Direct)]
    [InlineData(ProxyMode.Http)]
    [InlineData(ProxyMode.Socks5)]
    public void ProxyHttpClientFactory_CreateHandler_ConfiguresPooledConnectionIdleTimeoutAndLifetime(ProxyMode mode)
    {
        var config = new ProxyConfig
        {
            Mode = mode,
            Host = mode is ProxyMode.Http or ProxyMode.Socks5 ? "127.0.0.1" : string.Empty,
            Port = mode is ProxyMode.Http or ProxyMode.Socks5 ? 7890 : 0,
        };

        using var handler = _proxyFactory.CreateHandler(config);

        Assert.True(handler.PooledConnectionIdleTimeout > TimeSpan.Zero);
        Assert.True(handler.PooledConnectionIdleTimeout <= TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromSeconds(90), handler.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
    }

    private ProviderInstanceConfig CreateTestConfig() => new()
    {
        Id = "stream-opt-test",
        DisplayName = "Stream Opt LLM",
        Kind = ProviderKind.OpenAiCompatible,
        Enabled = true,
        BaseUrl = "https://mock-stream.local/v1",
        Model = "gpt-4o-mini",
        EncryptedApiKey = _protector.Protect("sk-test"),
        TimeoutSeconds = 30,
    };

    private static HttpResponseMessage CreateSseResponse(string sseContent)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseContent, Encoding.UTF8),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static HttpResponseMessage CreateStreamResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static IProxyHttpClientFactory CreateStubFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
        new StubProxyHttpClientFactory(responder);

    private sealed class StubProxyHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IProxyHttpClientFactory
    {
        public HttpClient CreateClient(ProxyConfig? overrideConfig = null, TimeSpan? timeout = null) =>
            new(new DelegateHandler(responder))
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(30),
            };

        public void InvalidateProxyCache()
        {
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private sealed class AsyncChunkStream(Action? onWaitingForData = null) : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        public void Enqueue(string utf8Text) =>
            _channel.Writer.TryWrite(Encoding.UTF8.GetBytes(utf8Text));

        public void Complete() => _channel.Writer.TryComplete();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_current is not null && _offset < _current.Length)
                {
                    var toCopy = Math.Min(buffer.Length, _current.Length - _offset);
                    _current.AsSpan(_offset, toCopy).CopyTo(buffer.Span);
                    _offset += toCopy;
                    if (_offset >= _current.Length)
                    {
                        _current = null;
                        _offset = 0;
                    }

                    return toCopy;
                }

                if (!_channel.Reader.TryRead(out var next))
                {
                    onWaitingForData?.Invoke();
                    if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return 0;
                    }

                    if (!_channel.Reader.TryRead(out next))
                    {
                        continue;
                    }
                }

                if (next.Length == 0)
                {
                    continue;
                }

                _current = next;
                _offset = 0;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System.Net;
using System.Net.Http;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// 応答ヘッダーの解放と本文の保留を、テストの合図で段階制御する応答です。
/// 要求の開始・本文読取りの開始を通知し、各段階へ渡された取消トークンと応答streamの解放を観測できます。
/// </summary>
internal sealed class StagedResponse : IDisposable
{
    private readonly HttpResponseMessage response;

    /// <param name="body">本文の先頭として返すバイト列。省略時は1バイトです。</param>
    /// <param name="holdBody">本文を返し終えた後に、<see cref="PendingBodyStream.Release"/> まで読取りを保留するか。falseでは返し終えた時点で終端です。</param>
    /// <param name="contentType">応答のContent-Type。省略時は付けません。</param>
    internal StagedResponse(byte[]? body = null, bool holdBody = true, string? contentType = null)
        : this(new PendingBodyStream(body ?? [65], holdBody), contentType)
    {
    }

    /// <param name="body">呼出し側が段階を制御する本文stream。</param>
    /// <param name="contentType">応答のContent-Type。省略時は付けません。</param>
    internal StagedResponse(Stream body, string? contentType = null)
    {
        BodyStream = body;
        response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        if (contentType != null)
        {
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }
    }

    internal TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseHeaders { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>応答本文のstreamです。</summary>
    internal Stream BodyStream { get; }

    /// <summary><see cref="PendingBodyStream"/> で構成した応答の本文です。</summary>
    internal PendingBodyStream Body => (PendingBodyStream)BodyStream;

    /// <summary>handlerへ渡された送信の取消トークンです。</summary>
    internal CancellationToken RequestCancellation { get; private set; }

    internal async Task<HttpResponseMessage> RespondAsync(CancellationToken cancellationToken)
    {
        RequestCancellation = cancellationToken;
        RequestStarted.TrySetResult();
        await ReleaseHeaders.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>保留をすべて解放し、後片付けの待機が止まらないようにします。</summary>
    internal void ReleaseAll()
    {
        ReleaseHeaders.TrySetResult();
        (BodyStream as PendingBodyStream)?.Release.TrySetResult(0);
    }

    public void Dispose() => response.Dispose();
}

/// <summary>
/// 実ソケットを使わずに応答の段階を制御するhandlerです。
/// URIごとの応答を <see cref="Stage"/> で登録でき、未登録のURIには既定の応答を返します。
/// </summary>
internal sealed class StagedResponseHandler : HttpMessageHandler
{
    private readonly Dictionary<string, StagedResponse> routes = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>未登録のURIへ返す応答です。</summary>
    internal StagedResponse Default { get; } = new();

    internal TaskCompletionSource RequestStarted => Default.RequestStarted;
    internal TaskCompletionSource ReleaseHeaders => Default.ReleaseHeaders;
    internal PendingBodyStream Body => Default.Body;
    internal CancellationToken RequestCancellation => Default.RequestCancellation;

    /// <summary>指定URIへの要求に返す段階制御の応答を登録します。</summary>
    internal StagedResponse Stage(Uri uri, byte[]? body = null, bool holdBody = true, string? contentType = null)
        => Register(uri, new StagedResponse(body, holdBody, contentType));

    /// <summary>指定URIへの要求に、呼出し側が制御する本文streamの応答を登録します。</summary>
    internal StagedResponse Stage(Uri uri, Stream body, string? contentType = null)
        => Register(uri, new StagedResponse(body, contentType));

    private StagedResponse Register(Uri uri, StagedResponse stage)
    {
        lock (gate)
        {
            routes[uri.AbsoluteUri] = stage;
        }
        return stage;
    }

    /// <summary>登録済みのすべての応答を解放します。</summary>
    internal void ReleaseAll()
    {
        StagedResponse[] stages;
        lock (gate)
        {
            stages = [.. routes.Values];
        }
        Default.ReleaseAll();
        foreach (StagedResponse stage in stages)
        {
            stage.ReleaseAll();
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        StagedResponse? stage;
        lock (gate)
        {
            routes.TryGetValue(request.RequestUri!.AbsoluteUri, out stage);
        }
        return (stage ?? Default).RespondAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Default.Dispose();
            lock (gate)
            {
                foreach (StagedResponse stage in routes.Values)
                {
                    stage.Dispose();
                }
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 先頭のバイト列を返した後、<see cref="Release"/> が完了するまで読取りを保留する応答本文streamです。
/// 保留中の読取りへ渡された取消トークンと、streamの解放を観測できます。
/// </summary>
internal sealed class PendingBodyStream : Stream
{
    private readonly byte[] prefix;
    private readonly bool holdAfterPrefix;
    private int prefixOffset;

    internal PendingBodyStream(byte[]? prefix = null, bool holdAfterPrefix = true)
    {
        this.prefix = prefix ?? [65];
        this.holdAfterPrefix = holdAfterPrefix;
    }

    internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>保留中の読取りの結果です。0は本文の終端を表します。</summary>
    internal TaskCompletionSource<int> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>保留中の読取りへ渡された取消トークンです。</summary>
    internal CancellationToken ReadCancellation { get; private set; }

    internal bool IsDisposed { get; private set; }

    public override bool CanRead => !IsDisposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
        {
            return ValueTask.FromResult(0);
        }
        if (prefixOffset < prefix.Length)
        {
            int count = Math.Min(buffer.Length, prefix.Length - prefixOffset);
            prefix.AsSpan(prefixOffset, count).CopyTo(buffer.Span);
            prefixOffset += count;
            return ValueTask.FromResult(count);
        }
        if (!holdAfterPrefix)
        {
            return ValueTask.FromResult(0);
        }
        ReadCancellation = cancellationToken;
        ReadStarted.TrySetResult();
        return new ValueTask<int>(Release.Task.WaitAsync(cancellationToken));
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }

    // 同期APIの本文読取りも、同じ保留と取消の観測を通します。
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        byte[] temporary = new byte[buffer.Length];
        int count = ReadAsync(temporary.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        temporary.AsSpan(0, count).CopyTo(buffer);
        return count;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

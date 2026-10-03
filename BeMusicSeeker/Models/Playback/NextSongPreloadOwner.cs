#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ribbit.BMS;

namespace BeMusicSeeker.Models;

/// <summary>譜面pathと軽量なファイル属性を使う、一回の準備要求の識別情報です。</summary>
internal sealed record NextSongPreloadInput(string Path, long Length, DateTime LastWriteTimeUtc)
{
    /// <summary>実対象を解決した時点の譜面属性を捕捉します。音源全件は走査しません。</summary>
    internal static NextSongPreloadInput Capture(string path)
    {
        var file = new FileInfo(System.IO.Path.GetFullPath(path));
        return new NextSongPreloadInput(file.FullName, file.Length, file.LastWriteTimeUtc);
    }

    /// <summary>Windowsのpath比較と属性の両方で同じ準備入力か確認します。</summary>
    internal bool Matches(NextSongPreloadInput? other) => other != null
        && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
        && Length == other.Length && LastWriteTimeUtc == other.LastWriteTimeUtc;
}

/// <summary>通常ロードを一曲分だけ前倒しし、準備Taskを一度だけ実再生へ渡します。</summary>
/// <remarks>登録・取得・取消は内蔵playerの開始停止gate内で直列に呼びます。背景処理はこの所有状態を変更しません。</remarks>
internal sealed class NextSongPreloadOwner
{
    private readonly Func<NextSongPreloadInput, CancellationToken, PreparedBmsSong> prepare;
    private NextSongPreloadInput? input;
    private CancellationTokenSource? cancellation;
    private Task<PreparedBmsSong>? work;

    /// <summary>通常ロードと同じ解析・復号処理を指定します。</summary>
    internal NextSongPreloadOwner(Func<NextSongPreloadInput, CancellationToken, PreparedBmsSong> prepare)
    {
        this.prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
    }

    /// <summary>空の枠に一件だけ準備します。候補の差替えや自動再試行は行いません。</summary>
    internal Task<PreparedBmsSong> Request(NextSongPreloadInput candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (work != null) { throw new InvalidOperationException("A next song is already prepared or preparing."); }
        var source = new CancellationTokenSource();
        input = candidate;
        cancellation = source;
        work = Task.Run(() =>
        {
            source.Token.ThrowIfCancellationRequested();
            PreparedBmsSong result = prepare(candidate, source.Token);
            source.Token.ThrowIfCancellationRequested();
            return result;
        });
        return work;
    }

    /// <summary>背景故障を処理する前に、実開始・停止がこのTaskを既に引き取っていないか確認します。</summary>
    internal bool Owns(Task<PreparedBmsSong> task) => ReferenceEquals(work, task);

    /// <summary>一致すれば準備中でも同じTaskを引き取ります。不一致なら取消後の終了を待って捨てます。</summary>
    internal async Task<PreparedBmsSong?> TakeAsync(NextSongPreloadInput? actual)
    {
        if (work == null) { return null; }
        Task<PreparedBmsSong> pending = work;
        CancellationTokenSource source = cancellation
            ?? throw new InvalidOperationException("The preparation cancellation source is missing.");
        bool matches = actual?.Matches(input) == true;
        try
        {
            if (matches) { return await pending.ConfigureAwait(false); }
            source.Cancel();
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception failure) when (IsInputFailure(failure)) { }
            return null;
        }
        finally
        {
            input = null;
            work = null;
            cancellation = null;
            source.Dispose();
        }
    }

    /// <summary>取消要求だけで終わらず、入力buffer・decoderの後片付けまで待ちます。fatalは呼出元へ返します。</summary>
    internal Task InvalidateAsync() => TakeAsync(null);

    /// <summary>通常の入力失敗だけを分類します。parser内部の原因分類はparser自身の契約を使います。</summary>
    internal static bool IsInputFailure(Exception failure) => failure switch
    {
        Ribbit.BMS.InvalidBmsonFileException => true,
        Ribbit.BMS.BMSFile.InvalidBmsFileException parsed => parsed.IsInputFailure,
        InvalidDataException or IOException or UnauthorizedAccessException => true,
        _ => false
    };
}

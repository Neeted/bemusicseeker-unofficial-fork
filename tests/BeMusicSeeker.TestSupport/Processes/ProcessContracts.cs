using System.Collections.ObjectModel;
using System.Diagnostics;

namespace BeMusicSeeker.TestSupport.Processes;

/// <summary>起動対象に適用する所有境界です。</summary>
public enum ProcessOwnershipPolicy
{
    /// <summary>起動前から Job に所属させ、所有者の異常終了でも子孫を回収します。</summary>
    Application,
    /// <summary>ビルドサーバーを無効化し、保持した Process のツリーを回収します。</summary>
    Preparation,
}

/// <summary>親環境を変更せず起動するための変更不能な要求です。</summary>
public sealed class ProcessStartRequest
{
    /// <summary>引数と環境を複製し、呼出元による起動後の変更を防ぎます。</summary>
    public ProcessStartRequest(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        ProcessOwnershipPolicy ownershipPolicy = ProcessOwnershipPolicy.Application)
    {
        FileName = fileName;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        WorkingDirectory = workingDirectory;
        Environment = new ReadOnlyDictionary<string, string?>(environment is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase));
        OwnershipPolicy = ownershipPolicy;
    }

    /// <summary>実行ファイルです。シェルによる解釈は行いません。</summary>
    public string FileName { get; }
    /// <summary>分割済みの引数です。</summary>
    public IReadOnlyList<string> Arguments { get; }
    /// <summary>子の作業ディレクトリです。</summary>
    public string WorkingDirectory { get; }
    /// <summary>子だけに適用する環境差分です。null 値は変数を削除します。</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }
    /// <summary>所有するプロセスの範囲です。</summary>
    public ProcessOwnershipPolicy OwnershipPolicy { get; }
}

/// <summary>単調時計に基づき、複数の待機で共有する絶対期限です。</summary>
public sealed class ProcessDeadline
{
    private readonly long started;
    private readonly TimeSpan timeout;

    private ProcessDeadline(TimeSpan timeout) : this(Stopwatch.GetTimestamp(), timeout) { }

    private ProcessDeadline(long started, TimeSpan timeout)
    {
        this.started = started;
        this.timeout = timeout;
    }

    /// <summary>現在を始点とする期限を一度だけ作ります。</summary>
    public static ProcessDeadline FromTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        return new ProcessDeadline(timeout);
    }

    /// <summary>期限をリセットせず残時間を返します。</summary>
    public TimeSpan Remaining
    {
        get
        {
            TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>待機継続の実行時刻ではなく、指定した終端観測時刻が期限内かを判定します。</summary>
    internal bool Includes(long timestamp) => Stopwatch.GetElapsedTime(started, timestamp) <= timeout;

    /// <summary>元の単調時計の始点を保ち、絶対期限に回収猶予を加えます。</summary>
    internal ProcessDeadline AddGrace(TimeSpan grace) => new(started, timeout + grace);

    /// <summary>元の絶対期限と、現在からの上限の早い方を一度確定します。</summary>
    internal ProcessDeadline LimitRemaining(TimeSpan maximum)
    {
        long now = Stopwatch.GetTimestamp();
        TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started, now);
        return new ProcessDeadline(now, remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining < maximum ? remaining : maximum);
    }
}

/// <summary>root、両出力、所有 Job の全子孫が終端した結果です。</summary>
/// <param name="ExitCode">root の終了コードです。</param>
/// <param name="StandardOutput">回収した UTF-8 標準出力です。</param>
/// <param name="StandardError">回収した UTF-8 標準エラーです。</param>
public sealed record ProcessCompletion(int ExitCode, string StandardOutput, string StandardError);

/// <summary>主失敗とは別に保存する停止・回収結果です。</summary>
/// <param name="CleanupFailure">正常終了要求、強制停止、回収で発生した失敗です。</param>
public sealed record ProcessStopResult(Exception? CleanupFailure);

/// <summary>起動自体に失敗した場合も、起動失敗と回収失敗を分離します。</summary>
public sealed class OwnedProcessStartException : Exception
{
    /// <summary>元の起動失敗と、存在する場合は回収失敗を保持します。</summary>
    public OwnedProcessStartException(Exception primaryFailure, Exception? cleanupFailure)
        : base("所有プロセスを起動できません。", primaryFailure) => CleanupFailure = cleanupFailure;

    /// <summary>起動失敗後の回収失敗です。</summary>
    public Exception? CleanupFailure { get; }
}

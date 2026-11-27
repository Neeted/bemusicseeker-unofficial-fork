using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>GitHubのスター依頼の所有者を、テストの構成と所有者のテストへ接続します。</summary>
internal static class GitHubStarPromptTestSupport
{
    /// <summary>保存値に触れない無効な所有者を作ります。スター依頼を対象としない起動の構成で使います。</summary>
    internal static GitHubStarPromptWorkflowOwner CreateDisabledOwner()
    {
        return new GitHubStarPromptWorkflowOwner(
            enabled: false,
            new RecordingGitHubStarPromptStore(GitHubStarPromptRecord.Initial) { RejectAccess = true },
            () => throw new InvalidOperationException("A disabled GitHub star prompt must not read the clock."),
            () => throw new InvalidOperationException("A disabled GitHub star prompt must not wait for the update terminal."),
            _ => throw new InvalidOperationException("A disabled GitHub star prompt must not dispatch to the UI."));
    }
}

/// <summary>
/// 保存値を記憶し、読取り・保存の回数と保存した値を記録する偽のストアです。失敗の注入とアクセスの禁止を持ちます。
/// </summary>
internal sealed class RecordingGitHubStarPromptStore : IGitHubStarPromptStore
{
    private readonly object syncRoot = new();

    private readonly List<GitHubStarPromptRecord> saves = [];

    private GitHubStarPromptRecord current;

    /// <summary>初期の保存値を持つストアを作ります。</summary>
    internal RecordingGitHubStarPromptStore(GitHubStarPromptRecord initial)
    {
        current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    /// <summary>真なら読取り・保存のどちらでもテストを失敗させる例外を投げます。</summary>
    internal bool RejectAccess { get; init; }

    /// <summary>読取りで投げる例外です。nullなら保存値を返します。</summary>
    internal Exception? ReadFailure { get; set; }

    /// <summary>保存の何回目（1始まり）で例外を投げるかと、その例外です。</summary>
    internal (int SaveNumber, Exception Failure)? SaveFailure { get; set; }

    /// <summary>最初の保存の試行で、試行した値で完了します。成功・失敗を問いません。</summary>
    internal TaskCompletionSource<GitHubStarPromptRecord> FirstSaveAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>読取りの回数です。</summary>
    internal int ReadCount { get; private set; }

    /// <summary>保存の試行回数です。</summary>
    internal int SaveAttemptCount { get; private set; }

    /// <summary>成功した保存の値を、保存した順に返します。</summary>
    internal IReadOnlyList<GitHubStarPromptRecord> Saves
    {
        get
        {
            lock (syncRoot)
            {
                return saves.ToArray();
            }
        }
    }

    /// <summary>現在の保存値です。</summary>
    internal GitHubStarPromptRecord Current
    {
        get
        {
            lock (syncRoot)
            {
                return current;
            }
        }
    }

    /// <inheritdoc />
    public GitHubStarPromptRecord Read()
    {
        lock (syncRoot)
        {
            if (RejectAccess)
            {
                throw new InvalidOperationException("The GitHub star prompt store must not be read.");
            }
            ReadCount++;
            if (ReadFailure != null)
            {
                throw ReadFailure;
            }
            return current;
        }
    }

    /// <inheritdoc />
    public void Save(GitHubStarPromptRecord record)
    {
        lock (syncRoot)
        {
            if (RejectAccess)
            {
                throw new InvalidOperationException("The GitHub star prompt store must not be saved.");
            }
            SaveAttemptCount++;
            FirstSaveAttempted.TrySetResult(record);
            if (SaveFailure is { } failure && failure.SaveNumber == SaveAttemptCount)
            {
                throw failure.Failure;
            }
            current = record;
            saves.Add(record);
        }
    }
}

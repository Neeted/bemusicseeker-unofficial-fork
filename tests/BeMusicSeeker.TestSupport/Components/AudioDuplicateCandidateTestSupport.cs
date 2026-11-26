using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>重複音源の整理候補一覧・確認・整理の各手順のテストが共有する、メモリ上の入力の準備です。</summary>
internal static class AudioDuplicateCandidateTestSupport
{
    /// <summary>ライブラリへの問合せを持たない、一覧の構築入力の代替です。TITLE・ARTISTはフォルダごとに指定できます。</summary>
    internal sealed class MemorySource(IReadOnlyDictionary<string, (string Title, string Artist)>? pairs = null) : IDuplicateMergeCandidateListSource
    {
        internal int ResolveCalls { get; private set; }

        public IReadOnlyDictionary<string, (string Title, string Artist)> ResolveDestinationDisplayPairs(IReadOnlyList<string> folders, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return folders.Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(folder => folder, folder => pairs != null && pairs.TryGetValue(folder, out (string Title, string Artist) pair) ? pair : (string.Empty, string.Empty), StringComparer.OrdinalIgnoreCase);
        }

        public Func<string, bool> CaptureRegisteredBmsRootPredicate() => _ => false;
    }

    /// <summary>フォルダ直下に所持譜面を一つ持つ、一フォルダのグループと衝突名を持つ探索結果を作ります。</summary>
    internal static AudioDuplicateFolder Folder(string folder, params string[] collisionNames)
    {
        ChartFile chart = new(ChartFileKind.Bms, Path.Combine(folder, "chart.bms"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, "title", "raw", "artist", string.Empty,
            Path.GetFileName(folder), string.Empty, string.Empty, null, 0, null);
        return new AudioDuplicateFolder(new DuplicateGroup([chart], [folder]) { Header = Path.GetFileName(folder) }, collisionNames.Length == 0 ? new[] { "x" } : collisionNames);
    }

    /// <summary>確認結果を作ります。要注意は同等でない名前を持ち、安全は全名が同等です。</summary>
    internal static AudioDuplicateFolderInspection Inspection(AudioDuplicateInspectionStage stage, string[] names, string[]? equivalent = null)
        => new(stage, [], names, equivalent ?? (stage == AudioDuplicateInspectionStage.Safe ? names : []));

    /// <summary>整理不可の確認結果を作ります。</summary>
    internal static AudioDuplicateFolderInspection Unorganizable(DuplicateMergeVerificationReasonKind kind, string folder, string relative = "")
        => new(AudioDuplicateInspectionStage.Unorganizable, [new DuplicateMergeVerificationReason(kind, [new DuplicateMergeFinding(folder, relative)])], [], []);

    /// <summary>探索結果を一覧へ採用し、整理候補一覧を表示中の状態にします。</summary>
    internal static void Adopt(AudioDuplicateCandidateListOwner list, object library, IEnumerable<AudioDuplicateFolder> folders, MemorySource? source = null)
    {
        list.AttachLibrary(library);
        list.SetTreeSelected(true);
        AudioDuplicateCandidateListPreparation preparation = AudioDuplicateCandidateListOwner.Prepare(
            library, folders, source ?? new MemorySource(), CancellationToken.None);
        Assert.IsTrue(list.Adopt(preparation));
    }

    /// <summary>確認結果を書き込みます（書き込んだフォルダのSELECTはオフになります）。</summary>
    internal static void Verify(AudioDuplicateCandidateListOwner list, AudioDuplicateFolder folder, AudioDuplicateFolderInspection inspection)
        => list.WriteVerifications([new AudioDuplicateVerificationWrite(folder.Group, inspection)]);

    /// <summary>フォルダの状態を取得します。</summary>
    internal static AudioDuplicateCandidateState State(AudioDuplicateCandidateListOwner list, AudioDuplicateFolder folder)
    {
        Assert.IsTrue(list.TryGetState(folder.Group, out AudioDuplicateCandidateState? state));
        return state!;
    }
}

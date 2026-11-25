using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models;

/// <summary>KEEPに依存する理由の材料です。一つのフォルダが移動元になる場合にだけ該当する事実を、フォルダ別に保持します。</summary>
/// <param name="ParentReferences">そのフォルダの直下・配下の所持譜面が持つ、使用中の親ディレクトリ参照です。</param>
/// <param name="NestedChartFolders">そのフォルダの配下の子孫フォルダで、所持譜面を持つものです。</param>
internal sealed record DuplicateMergeKeepDependentFacts(
    IReadOnlyList<DuplicateMergeFinding> ParentReferences, IReadOnlyList<DuplicateMergeFinding> NestedChartFolders)
{
    /// <summary>該当する事実がない場合です。</summary>
    internal static DuplicateMergeKeepDependentFacts Empty { get; } = new([], []);
}

/// <summary>
/// 一つのグループを走査した結果の事実です。KEEPによらない理由と、KEEPに依存する理由の材料をフォルダ別に保ちます。
/// KEEPが変わっても再走査せず、<see cref="Evaluate"/> で段階と理由を導き直します。
/// </summary>
internal sealed class DuplicateMergeGroupInspection
{
    private readonly IReadOnlyDictionary<DuplicateMergeVerificationReasonKind, IReadOnlyList<DuplicateMergeFinding>> independentReasons;
    private readonly IReadOnlyList<string> protectedNameCollisions;
    private readonly DuplicateMergeComparisonSummary comparisons;
    private readonly IReadOnlyDictionary<string, DuplicateMergeKeepDependentFacts> keepDependentFacts;
    private readonly IReadOnlyList<string> equivalentAudioNames;
    private readonly bool allAudioCollisionsEquivalent;

    internal DuplicateMergeGroupInspection(
        IReadOnlyDictionary<DuplicateMergeVerificationReasonKind, IReadOnlyList<DuplicateMergeFinding>> independentReasons,
        IReadOnlyList<string> protectedNameCollisions,
        DuplicateMergeComparisonSummary comparisons,
        IReadOnlyDictionary<string, DuplicateMergeKeepDependentFacts> keepDependentFacts,
        IReadOnlyList<string> equivalentAudioNames = null,
        bool allAudioCollisionsEquivalent = false)
    {
        this.independentReasons = independentReasons ?? throw new ArgumentNullException(nameof(independentReasons));
        this.protectedNameCollisions = protectedNameCollisions ?? [];
        this.comparisons = comparisons ?? DuplicateMergeComparisonSummary.None;
        this.keepDependentFacts = keepDependentFacts ?? new Dictionary<string, DuplicateMergeKeepDependentFacts>();
        this.equivalentAudioNames = equivalentAudioNames ?? [];
        this.allAudioCollisionsEquivalent = allAudioCollisionsEquivalent;
    }

    /// <summary>
    /// 指定したフォルダをKEEPとしたときの確認結果を導きます。KEEP以外のフォルダが移動元になるため、
    /// 親ディレクトリ参照と配下の別の譜面フォルダは、KEEP以外のフォルダの事実だけから導きます。
    /// </summary>
    /// <param name="keepFolder">残すフォルダ。グループのフォルダと同じ綴りで、大文字小文字は区別しません。</param>
    internal DuplicateMergeVerificationResult Evaluate(string keepFolder)
    {
        var findings = new Dictionary<DuplicateMergeVerificationReasonKind, IReadOnlyList<DuplicateMergeFinding>>(independentReasons);
        var parentReferences = new List<DuplicateMergeFinding>();
        var nestedFolders = new List<DuplicateMergeFinding>();
        foreach ((string folder, DuplicateMergeKeepDependentFacts facts) in keepDependentFacts)
        {
            if (string.Equals(folder, keepFolder, StringComparison.OrdinalIgnoreCase)) { continue; }
            parentReferences.AddRange(facts.ParentReferences);
            nestedFolders.AddRange(facts.NestedChartFolders);
        }
        if (parentReferences.Count > 0) { findings[DuplicateMergeVerificationReasonKind.ParentReference] = Sort(parentReferences); }
        if (nestedFolders.Count > 0) { findings[DuplicateMergeVerificationReasonKind.NestedChartFolder] = Sort(nestedFolders); }

        DuplicateMergeVerificationReason[] reasons =
        [
            .. findings
                .Where(pair => pair.Value.Count > 0)
                .OrderBy(pair => pair.Key)
                .Select(pair => new DuplicateMergeVerificationReason(pair.Key, pair.Value))
        ];
        DuplicateMergeVerificationStage stage = reasons.Any(reason => reason.IsUnmergeable)
            ? DuplicateMergeVerificationStage.Unmergeable
            : reasons.Length > 0 ? DuplicateMergeVerificationStage.Caution : DuplicateMergeVerificationStage.Safe;
        return new DuplicateMergeVerificationResult(
            stage, reasons, protectedNameCollisions, comparisons, equivalentAudioNames, allAudioCollisionsEquivalent);
    }

    /// <summary>該当の並びを、フォルダのPATH昇順、次に相対パスの昇順（どちらも大文字小文字を無視）にします。</summary>
    internal static IReadOnlyList<DuplicateMergeFinding> Sort(IEnumerable<DuplicateMergeFinding> source)
        => [.. source
            .OrderBy(finding => finding.Folder ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.RelativePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Detail ?? string.Empty, StringComparer.OrdinalIgnoreCase)];
}

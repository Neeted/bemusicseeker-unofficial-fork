using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 重複 chart の解析結果です。
/// BMS storage row への warning 反映対象と、duplicate tree に表示する chart group を分けて保持します。
/// </summary>
internal sealed class DuplicateAnalysisResult
{
    public HashSet<BMSFile> DuplicateBmsFiles { get; } = [];

    public List<DuplicateGroup> DuplicateGroups { get; } = [];
}

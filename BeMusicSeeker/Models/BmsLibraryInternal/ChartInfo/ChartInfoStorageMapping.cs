using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>詳細情報のDB読取り境界で、保存行を不変値へ一度だけ変換します。</summary>
internal static class ChartInfoStorageMapping
{
    /// <summary>保存行の全列を解析・索引用の不変値へ変換します。</summary>
    internal static ChartDetails ToCommon(LR2SongDBExtended.chart_info row)
    {
        return row == null ? null : new ChartDetails
        {
            sha256 = row.sha256,
            md5 = row.md5,
            charthash = row.charthash,
            level = row.level,
            difficulty = row.difficulty,
            difficulty_defined = row.difficulty_defined,
            mainbpm = row.mainbpm,
            maxbpm = row.maxbpm,
            minbpm = row.minbpm,
            length = row.length,
            mode = row.mode,
            judge = row.judge,
            bga = row.bga,
            exlevel = row.exlevel,
            feature = row.feature,
            notes = row.notes,
            n = row.n,
            ln = row.ln,
            s = row.s,
            ls = row.ls,
            total = row.total,
            total_defined = row.total_defined,
            density = row.density,
            peakdensity = row.peakdensity,
            enddensity = row.enddensity,
            distribution = row.distribution,
            speedchange = row.speedchange,
            speedchange_count = row.speedchange_count,
            lanenotes = row.lanenotes,
            parser_version = row.parser_version,
            updated_at = row.updated_at,
        };
    }
    /// <summary>保存行の全列を解析・索引用の不変値へ変換します。</summary>
    internal static ChartParseFailure ToCommon(LR2SongDBExtended.chart_info_parse_failure row)
    {
        return row == null ? null : new ChartParseFailure
        {
            md5 = row.md5,
            sha256 = row.sha256,
            path = row.path,
            parser_version = row.parser_version,
            failure_kind = row.failure_kind,
            exception_type = row.exception_type,
            message = row.message,
            parse_timeout_ms = row.parse_timeout_ms,
            updated_at = row.updated_at,
        };
    }
    /// <summary>不変値を既存の保存列へ変換します。DB準備・書込みの境界でだけ使います。</summary>
    internal static LR2SongDBExtended.chart_info ToStorage(ChartDetails value)
    {
        return value == null ? null : new LR2SongDBExtended.chart_info
        {
            sha256 = value.sha256,
            md5 = value.md5,
            charthash = value.charthash,
            level = value.level,
            difficulty = value.difficulty,
            difficulty_defined = value.difficulty_defined,
            mainbpm = value.mainbpm,
            maxbpm = value.maxbpm,
            minbpm = value.minbpm,
            length = value.length,
            mode = value.mode,
            judge = value.judge,
            bga = value.bga,
            exlevel = value.exlevel,
            feature = value.feature,
            notes = value.notes,
            n = value.n,
            ln = value.ln,
            s = value.s,
            ls = value.ls,
            total = value.total,
            total_defined = value.total_defined,
            density = value.density,
            peakdensity = value.peakdensity,
            enddensity = value.enddensity,
            distribution = value.distribution,
            speedchange = value.speedchange,
            speedchange_count = value.speedchange_count,
            lanenotes = value.lanenotes,
            parser_version = value.parser_version,
            updated_at = value.updated_at,
        };
    }
    /// <summary>不変値を既存の保存列へ変換します。DB準備・書込みの境界でだけ使います。</summary>
    internal static LR2SongDBExtended.chart_info_parse_failure ToStorage(ChartParseFailure value)
    {
        return value == null ? null : new LR2SongDBExtended.chart_info_parse_failure
        {
            md5 = value.md5,
            sha256 = value.sha256,
            path = value.path,
            parser_version = value.parser_version,
            failure_kind = value.failure_kind,
            exception_type = value.exception_type,
            message = value.message,
            parse_timeout_ms = value.parse_timeout_ms,
            updated_at = value.updated_at,
        };
    }
}

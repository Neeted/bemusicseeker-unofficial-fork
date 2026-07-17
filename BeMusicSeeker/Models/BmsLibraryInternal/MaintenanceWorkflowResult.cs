using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class MaintenanceWorkflowResult
{
    public bool HasUpdates { get; set; }

    public int CheckedFileCount { get; set; }

    public int BmsResourceTargetCount { get; set; }

    public int BmsonResourceTargetCount { get; set; }

    public int HealthTargetCount { get; set; }

    public int HealthDegree { get; set; }

    public int ReaderDegree { get; set; }

    public int ReadQueueCapacity { get; set; }

    public int ComputedQueueCapacity { get; set; }

    public int ForceTargetCount { get; set; }

    public int MissingInfoTargetCount { get; set; }

    public int MissingEncodingTargetCount { get; set; }

    public int BmsonMissingFreshResourceReferenceCount { get; set; }

    public long HealthMs { get; set; }

    public long EncodingMs { get; set; }

    public long BmsonRefreshMs { get; set; }

    public long HealthCacheHitCount { get; set; }

    public long HealthFileExistsFallbackCount { get; set; }

    public long HealthAudioFileExistsFallbackCount { get; set; }

    public long HealthImageFileExistsFallbackCount { get; set; }

    public long HealthMovieFileExistsFallbackCount { get; set; }

    public long HealthOptionalImageFileExistsFallbackCount { get; set; }

    public int BmsonReparsedCount { get; set; }

    public int BmsonReparseFailedCount { get; set; }

    public int BmsonResourceReferenceReusedCount { get; set; }

    public int MaintenanceInfoUpsertCount { get; set; }

    public int MaintenanceInfoUnchangedCount { get; set; }

    public int SongUpsertCount { get; set; }

    public int ReloadedSongCount { get; set; }

    public int ZeroNoteChangedCount { get; set; }

    public long ReadMs { get; set; }

    public long DigestMs { get; set; }

    public long ComputeMs { get; set; }

    public long CommitMs { get; set; }

    public long ResourceHealthIndexMs { get; set; }

    public int WarningReapplyTargets { get; set; }

    public int WarningChangedCount { get; set; }

    public bool Canceled { get; set; }

    public long TotalMs { get; set; }
}

/// <summary>
/// Catalog maintenance owner から composition へ渡す immutable workflow facts です。
/// </summary>
internal sealed class MaintenanceWorkflowResultFacts
{
    private MaintenanceWorkflowResultFacts(MaintenanceWorkflowResult source)
    {
        HasUpdates = source?.HasUpdates == true;
        CheckedFileCount = source?.CheckedFileCount ?? 0;
        BmsResourceTargetCount = source?.BmsResourceTargetCount ?? 0;
        BmsonResourceTargetCount = source?.BmsonResourceTargetCount ?? 0;
        HealthTargetCount = source?.HealthTargetCount ?? 0;
        HealthDegree = source?.HealthDegree ?? 0;
        ReaderDegree = source?.ReaderDegree ?? 0;
        ReadQueueCapacity = source?.ReadQueueCapacity ?? 0;
        ComputedQueueCapacity = source?.ComputedQueueCapacity ?? 0;
        ForceTargetCount = source?.ForceTargetCount ?? 0;
        MissingInfoTargetCount = source?.MissingInfoTargetCount ?? 0;
        MissingEncodingTargetCount = source?.MissingEncodingTargetCount ?? 0;
        BmsonMissingFreshResourceReferenceCount = source?.BmsonMissingFreshResourceReferenceCount ?? 0;
        HealthMs = source?.HealthMs ?? 0L;
        EncodingMs = source?.EncodingMs ?? 0L;
        BmsonRefreshMs = source?.BmsonRefreshMs ?? 0L;
        HealthCacheHitCount = source?.HealthCacheHitCount ?? 0L;
        HealthFileExistsFallbackCount = source?.HealthFileExistsFallbackCount ?? 0L;
        HealthAudioFileExistsFallbackCount = source?.HealthAudioFileExistsFallbackCount ?? 0L;
        HealthImageFileExistsFallbackCount = source?.HealthImageFileExistsFallbackCount ?? 0L;
        HealthMovieFileExistsFallbackCount = source?.HealthMovieFileExistsFallbackCount ?? 0L;
        HealthOptionalImageFileExistsFallbackCount = source?.HealthOptionalImageFileExistsFallbackCount ?? 0L;
        BmsonReparsedCount = source?.BmsonReparsedCount ?? 0;
        BmsonReparseFailedCount = source?.BmsonReparseFailedCount ?? 0;
        BmsonResourceReferenceReusedCount = source?.BmsonResourceReferenceReusedCount ?? 0;
        MaintenanceInfoUpsertCount = source?.MaintenanceInfoUpsertCount ?? 0;
        MaintenanceInfoUnchangedCount = source?.MaintenanceInfoUnchangedCount ?? 0;
        SongUpsertCount = source?.SongUpsertCount ?? 0;
        ReloadedSongCount = source?.ReloadedSongCount ?? 0;
        ZeroNoteChangedCount = source?.ZeroNoteChangedCount ?? 0;
        ReadMs = source?.ReadMs ?? 0L;
        DigestMs = source?.DigestMs ?? 0L;
        ComputeMs = source?.ComputeMs ?? 0L;
        CommitMs = source?.CommitMs ?? 0L;
        ResourceHealthIndexMs = source?.ResourceHealthIndexMs ?? 0L;
        WarningReapplyTargets = source?.WarningReapplyTargets ?? 0;
        WarningChangedCount = source?.WarningChangedCount ?? 0;
        Canceled = source?.Canceled == true;
        TotalMs = source?.TotalMs ?? 0L;
    }

    internal bool HasUpdates { get; }
    internal int CheckedFileCount { get; }
    internal int BmsResourceTargetCount { get; }
    internal int BmsonResourceTargetCount { get; }
    internal int HealthTargetCount { get; }
    internal int HealthDegree { get; }
    internal int ReaderDegree { get; }
    internal int ReadQueueCapacity { get; }
    internal int ComputedQueueCapacity { get; }
    internal int ForceTargetCount { get; }
    internal int MissingInfoTargetCount { get; }
    internal int MissingEncodingTargetCount { get; }
    internal int BmsonMissingFreshResourceReferenceCount { get; }
    internal long HealthMs { get; }
    internal long EncodingMs { get; }
    internal long BmsonRefreshMs { get; }
    internal long HealthCacheHitCount { get; }
    internal long HealthFileExistsFallbackCount { get; }
    internal long HealthAudioFileExistsFallbackCount { get; }
    internal long HealthImageFileExistsFallbackCount { get; }
    internal long HealthMovieFileExistsFallbackCount { get; }
    internal long HealthOptionalImageFileExistsFallbackCount { get; }
    internal int BmsonReparsedCount { get; }
    internal int BmsonReparseFailedCount { get; }
    internal int BmsonResourceReferenceReusedCount { get; }
    internal int MaintenanceInfoUpsertCount { get; }
    internal int MaintenanceInfoUnchangedCount { get; }
    internal int SongUpsertCount { get; }
    internal int ReloadedSongCount { get; }
    internal int ZeroNoteChangedCount { get; }
    internal long ReadMs { get; }
    internal long DigestMs { get; }
    internal long ComputeMs { get; }
    internal long CommitMs { get; }
    internal long ResourceHealthIndexMs { get; }
    internal int WarningReapplyTargets { get; }
    internal int WarningChangedCount { get; }
    internal bool Canceled { get; }
    internal long TotalMs { get; }

    internal static MaintenanceWorkflowResultFacts From(MaintenanceWorkflowResult source)
    {
        return new MaintenanceWorkflowResultFacts(source);
    }

    internal MaintenanceWorkflowResult ToMutable()
    {
        return new MaintenanceWorkflowResult
        {
            HasUpdates = HasUpdates,
            CheckedFileCount = CheckedFileCount,
            BmsResourceTargetCount = BmsResourceTargetCount,
            BmsonResourceTargetCount = BmsonResourceTargetCount,
            HealthTargetCount = HealthTargetCount,
            HealthDegree = HealthDegree,
            ReaderDegree = ReaderDegree,
            ReadQueueCapacity = ReadQueueCapacity,
            ComputedQueueCapacity = ComputedQueueCapacity,
            ForceTargetCount = ForceTargetCount,
            MissingInfoTargetCount = MissingInfoTargetCount,
            MissingEncodingTargetCount = MissingEncodingTargetCount,
            BmsonMissingFreshResourceReferenceCount = BmsonMissingFreshResourceReferenceCount,
            HealthMs = HealthMs,
            EncodingMs = EncodingMs,
            BmsonRefreshMs = BmsonRefreshMs,
            HealthCacheHitCount = HealthCacheHitCount,
            HealthFileExistsFallbackCount = HealthFileExistsFallbackCount,
            HealthAudioFileExistsFallbackCount = HealthAudioFileExistsFallbackCount,
            HealthImageFileExistsFallbackCount = HealthImageFileExistsFallbackCount,
            HealthMovieFileExistsFallbackCount = HealthMovieFileExistsFallbackCount,
            HealthOptionalImageFileExistsFallbackCount = HealthOptionalImageFileExistsFallbackCount,
            BmsonReparsedCount = BmsonReparsedCount,
            BmsonReparseFailedCount = BmsonReparseFailedCount,
            BmsonResourceReferenceReusedCount = BmsonResourceReferenceReusedCount,
            MaintenanceInfoUpsertCount = MaintenanceInfoUpsertCount,
            MaintenanceInfoUnchangedCount = MaintenanceInfoUnchangedCount,
            SongUpsertCount = SongUpsertCount,
            ReloadedSongCount = ReloadedSongCount,
            ZeroNoteChangedCount = ZeroNoteChangedCount,
            ReadMs = ReadMs,
            DigestMs = DigestMs,
            ComputeMs = ComputeMs,
            CommitMs = CommitMs,
            ResourceHealthIndexMs = ResourceHealthIndexMs,
            WarningReapplyTargets = WarningReapplyTargets,
            WarningChangedCount = WarningChangedCount,
            Canceled = Canceled,
            TotalMs = TotalMs
        };
    }
}

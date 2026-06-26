using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal enum ScoreViewerRegistrationItemStatus
{
    HashOnly,
    AlreadyRegistered,
    NeedsUpload,
    Uploaded,
    StatusCheckFailed,
    UploadFailed,
    UploadDeclined,
}

internal sealed class ScoreViewerRegistrationItem
{
    private ScoreViewerRegistrationItem(
        ScoreViewerTarget target,
        ScoreViewerRegistrationItemStatus status,
        string hash,
        string viewUrl,
        string failureMessage,
        Exception exception)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Status = status;
        Hash = hash ?? string.Empty;
        ViewUrl = viewUrl;
        FailureMessage = failureMessage;
        Exception = exception;
    }

    internal ScoreViewerTarget Target { get; }

    internal ScoreViewerRegistrationItemStatus Status { get; }

    internal string Hash { get; }

    internal string ViewUrl { get; }

    internal string FailureMessage { get; }

    internal Exception Exception { get; }

    internal bool IsUploadCandidate => Status == ScoreViewerRegistrationItemStatus.NeedsUpload;

    internal bool IsFailure => Status is ScoreViewerRegistrationItemStatus.StatusCheckFailed or ScoreViewerRegistrationItemStatus.UploadFailed;

    internal bool IsUploaded => Status == ScoreViewerRegistrationItemStatus.Uploaded;

    internal bool IsUploadDeclined => Status == ScoreViewerRegistrationItemStatus.UploadDeclined;

    internal static ScoreViewerRegistrationItem HashOnly(ScoreViewerTarget target, string hash, string viewUrl)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.HashOnly, hash, viewUrl, null, null);
    }

    internal static ScoreViewerRegistrationItem AlreadyRegistered(ScoreViewerTarget target, string hash, string viewUrl)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.AlreadyRegistered, hash, viewUrl, null, null);
    }

    internal static ScoreViewerRegistrationItem NeedsUpload(ScoreViewerTarget target, string hash)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.NeedsUpload, hash, null, null, null);
    }

    internal static ScoreViewerRegistrationItem Uploaded(ScoreViewerTarget target, string hash, string viewUrl)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.Uploaded, hash, viewUrl, null, null);
    }

    internal static ScoreViewerRegistrationItem StatusCheckFailed(ScoreViewerTarget target, string hash, Exception exception)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.StatusCheckFailed, hash, null, exception?.Message, exception);
    }

    internal static ScoreViewerRegistrationItem UploadFailed(ScoreViewerTarget target, string hash, string failureMessage, Exception exception = null)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.UploadFailed, hash, null, failureMessage, exception);
    }

    internal static ScoreViewerRegistrationItem UploadDeclined(ScoreViewerTarget target, string hash)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.UploadDeclined, hash, null, null, null);
    }
}

internal sealed class ScoreViewerRegistrationPlan
{
    internal ScoreViewerRegistrationPlan(IReadOnlyList<ScoreViewerRegistrationItem> items)
    {
        Items = items ?? [];
    }

    internal IReadOnlyList<ScoreViewerRegistrationItem> Items { get; }

    internal int TargetCount => Items.Count;

    internal IReadOnlyList<ScoreViewerRegistrationItem> UploadCandidates => [.. Items.Where(item => item.IsUploadCandidate)];

    internal int UploadCandidateCount => Items.Count(item => item.IsUploadCandidate);

    internal bool HasUploadCandidates => UploadCandidateCount > 0;
}

internal sealed class ScoreViewerRegistrationResult
{
    internal ScoreViewerRegistrationResult(IReadOnlyList<ScoreViewerRegistrationItem> items)
    {
        Items = items ?? [];
    }

    internal IReadOnlyList<ScoreViewerRegistrationItem> Items { get; }

    internal string LastViewUrl => Items.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.ViewUrl))?.ViewUrl;

    internal int UploadedCount => Items.Count(item => item.IsUploaded);

    internal int FailureCount => Items.Count(item => item.IsFailure);

    internal int UploadDeclinedCount => Items.Count(item => item.IsUploadDeclined);

    internal bool HasUploadedRegistration => UploadedCount > 0;

    internal bool HasFailures => FailureCount > 0;

    internal bool HasUploadDeclined => UploadDeclinedCount > 0;
}

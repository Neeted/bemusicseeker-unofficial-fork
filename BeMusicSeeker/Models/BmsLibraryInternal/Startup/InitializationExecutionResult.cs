namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>名前のある必須手順と公開継続の実終端に要した計測です。</summary>
internal sealed class InitializationExecutionResult
{
    public long SavedDataMs { get; set; }

    public long FilesAndProjectionMs { get; set; }

    public long WaitContinuationMs { get; set; }

    public long WaitBeforeContinuationStartMs { get; set; }

    public long WaitForContinuationSignalMs { get; set; }

    public long WaitForContinuationTasksMs { get; set; }

    public long TotalMs { get; set; }
}

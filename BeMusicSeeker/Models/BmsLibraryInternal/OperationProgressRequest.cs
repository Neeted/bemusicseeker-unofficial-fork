namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>要求の受付時に捕捉した表示世代、発生元操作、要求元とその主体内の版を保持します。</summary>
/// <param name="Generation">捕捉した表示世代。</param>
/// <param name="OperationToken">発生元操作。独立した要求では0です。</param>
/// <param name="Source">要求版を発行した主体と処理。</param>
/// <param name="Version">同じ要求元だけで比較する要求版。</param>
internal sealed record OperationProgressRequest(long Generation, long OperationToken, string Source, long Version);

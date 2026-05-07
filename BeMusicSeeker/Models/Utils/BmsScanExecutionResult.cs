namespace BeMusicSeeker.Models.Utils;

public class BmsScanExecutionResult
{
	public bool Success { get; set; }

	public string ErrorReason { get; set; }

	public BmsScanResult Result { get; set; }

	internal LibraryResourceIndex ResourceIndex { get; set; }

	public long ConnectMs { get; set; }

	public long BmsQueryMs { get; set; }

	public long BmsSearchMs { get; set; }

	public long BmsReadMs { get; set; }

	public long SiblingQueryMs { get; set; }

	public long SiblingSearchMs { get; set; }

	public long SiblingReadMs { get; set; }

	public long BuildResultMs { get; set; }

	public long HashBuildMs { get; set; }

	public ulong HashDirCount { get; set; }

	public ulong CategoryResourceKeyHashEntryCount { get; set; }

	public bool NativeBridgeUsed { get; set; }

	public long NativeBridgeMs { get; set; }

	public string NativeBridgeReason { get; set; }

	public long ManagedDecodeMs { get; set; }

	public long ManagedMaterializeMs { get; set; }

	public ulong BridgeRawBufferBytes { get; set; }

	public ulong BmsQueryHitCount { get; set; }

	public ulong SiblingQueryHitCount { get; set; }

	public ulong ChartQueryHitCount { get; set; }

	public ulong AudioQueryHitCount { get; set; }

	public ulong ImageQueryHitCount { get; set; }

	public ulong MovieQueryHitCount { get; set; }

	public long ChartQueryMs { get; set; }

	public long AudioQueryMs { get; set; }

	public long ImageQueryMs { get; set; }

	public long MovieQueryMs { get; set; }

	public long AssignMs { get; set; }

	public long DedupeMs { get; set; }

	public long PackMs { get; set; }

	public long PackReverseBuildMs { get; set; }

	public long AudioReverseBuildMs { get; set; }

	public long ImageReverseBuildMs { get; set; }

	public long MovieReverseBuildMs { get; set; }

	public long PackLayoutMs { get; set; }

	public long PackAllocMs { get; set; }

	public long PackWriteMs { get; set; }

	public uint ReverseIndexBytes { get; set; }

	public long ChartSearchMs { get; set; }

	public long ChartReadMs { get; set; }

	public long AudioSearchMs { get; set; }

	public long AudioReadMs { get; set; }

	public long ImageSearchMs { get; set; }

	public long ImageReadMs { get; set; }

	public long MovieSearchMs { get; set; }

	public long MovieReadMs { get; set; }

	public long ChartSdkReadMs { get; set; }

	public long ChartCallbackMs { get; set; }

	public long AudioSdkReadMs { get; set; }

	public long AudioCallbackMs { get; set; }

	public long ImageSdkReadMs { get; set; }

	public long ImageCallbackMs { get; set; }

	public long MovieSdkReadMs { get; set; }

	public long MovieCallbackMs { get; set; }

	public ulong ChartPathResizeCount { get; set; }

	public ulong ChartNameResizeCount { get; set; }

	public ulong AudioPathResizeCount { get; set; }

	public ulong AudioNameResizeCount { get; set; }

	public ulong ImagePathResizeCount { get; set; }

	public ulong ImageNameResizeCount { get; set; }

	public ulong MoviePathResizeCount { get; set; }

	public ulong MovieNameResizeCount { get; set; }

	public ulong ChartDirectoryCount { get; set; }

	public ulong AudioAssignedCount { get; set; }

	public ulong ImageAssignedCount { get; set; }

	public ulong MovieAssignedCount { get; set; }

	public ulong AudioResourceKeyHashCount { get; set; }

	public ulong ImageResourceKeyHashCount { get; set; }

	public ulong MovieResourceKeyHashCount { get; set; }

	public ulong AudioResourceDirCount { get; set; }

	public ulong ImageResourceDirCount { get; set; }

	public ulong MovieResourceDirCount { get; set; }

	public ulong OwnerCacheHitCount { get; set; }

	public ulong OwnerCacheMissCount { get; set; }

	public ulong RelativePrefixCacheHitCount { get; set; }

	public ulong RelativePrefixCacheMissCount { get; set; }

	public long AudioGroupMs { get; set; }

	public long AudioAssignMs { get; set; }

	public long AudioMergeMs { get; set; }

	public long ImageGroupMs { get; set; }

	public long ImageAssignMs { get; set; }

	public long ImageMergeMs { get; set; }

	public long MovieGroupMs { get; set; }

	public long MovieAssignMs { get; set; }

	public long MovieMergeMs { get; set; }
}

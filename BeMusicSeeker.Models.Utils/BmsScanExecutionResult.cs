namespace BeMusicSeeker.Models.Utils;

public class BmsScanExecutionResult
{
	public bool Success { get; set; }

	public string ErrorReason { get; set; }

	public BmsScanResult Result { get; set; }

	public long ConnectMs { get; set; }

	public long BmsQueryMs { get; set; }

	public long BmsSearchMs { get; set; }

	public long BmsReadMs { get; set; }

	public long SiblingQueryMs { get; set; }

	public long SiblingSearchMs { get; set; }

	public long SiblingReadMs { get; set; }

	public long BuildResultMs { get; set; }

	public ulong BmsQueryHitCount { get; set; }

	public ulong SiblingQueryHitCount { get; set; }
}

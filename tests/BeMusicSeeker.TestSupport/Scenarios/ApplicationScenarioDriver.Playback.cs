using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Scenarios;

internal sealed partial class ApplicationScenarioDriver
{
    /// <summary>一回の通常入力と同じ業務Task・実開始・PCMを待ちます。自然Completionは明示した呼出しでのみ待ちます。</summary>
    internal async Task<ScenarioPlaybackReceipt> PlaybackAsync(ScenarioPlaybackAction action, string chartPath,
        ProcessDeadline deadline, bool waitForCompletion = false) =>
        (await RequestAsync<ScenarioPlaybackCompleted>(id => new ScenarioPlaybackCommand(id, action, chartPath, waitForCompletion), deadline)).Playback;
}

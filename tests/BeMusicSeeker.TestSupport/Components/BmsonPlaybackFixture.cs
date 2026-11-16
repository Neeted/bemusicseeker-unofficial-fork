using BeMusicSeeker.TestSupport.Files;
using Ribbit.BMS;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数moduleで共有する既存音声テストの準備・観測境界を一か所で所有します。</summary>
internal static class BmsonPlaybackFixture
{
    /// <summary>登録順・LN終端の包含を区別する独立入力です。125 BPM/240 resolutionで1pulseは0.002秒です。</summary>
    internal static string ConflictingPlayableInput(string scenario)
    {
        string Channel(string notes) => "{\"name\":\"a.wav\",\"notes\":[" + notes + "]}";
        const string normal = "{\"x\":1,\"y\":240}";
        const string shortLn = "{\"x\":1,\"y\":240,\"l\":240}";
        const string longLn = "{\"x\":1,\"y\":240,\"l\":480}";
        string channels = scenario switch
        {
            "normal-ln" => Channel(normal) + "," + Channel(shortLn),
            "ln-normal" => Channel(shortLn) + "," + Channel(normal),
            "short-long" => Channel(shortLn) + "," + Channel(longLn),
            "long-short" => Channel(longLn) + "," + Channel(shortLn),
            "stable-channel-order" => Channel("{\"x\":1,\"y\":720},{\"x\":1,\"y\":240,\"l\":480},"
                + "{\"x\":2,\"y\":240,\"l\":240},{\"x\":2,\"y\":240,\"l\":480},"
                + "{\"x\":3,\"y\":240,\"l\":480},{\"x\":3,\"y\":240,\"l\":240}")
                + "," + Channel("{\"x\":1,\"y\":480},{\"x\":1,\"y\":600,\"l\":480},"
                + "{\"x\":1,\"y\":720,\"l\":240},{\"x\":1,\"y\":721},{\"x\":4,\"y\":480}"),
            "later-channel-earlier-head" => Channel("{\"x\":1,\"y\":480},{\"x\":2,\"y\":600,\"l\":120},{\"x\":3,\"y\":720}")
                + "," + Channel("{\"x\":1,\"y\":240,\"l\":480},{\"x\":2,\"y\":240,\"l\":480},"
                + "{\"x\":3,\"y\":240,\"l\":480},{\"x\":4,\"y\":240,\"l\":480}"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        return "{\"info\":{\"init_bpm\":125,\"resolution\":240},\"lines\":[],\"sound_channels\":[" + channels + "]}";
    }

    /// <summary>一つの専有入力ファイルを通常読込みで解析し、結果を取得後に領域を解放します。</summary>
    internal static PlaybackChart Parse(string json)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = workspace.RootDirectory;
        string path = Path.Combine(directory, "test.bmson");
        File.WriteAllText(path, json);
        return PlaybackChart.Load(path);
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static string LayeredPlayableInput() =>
        "{\"version\":\"1.0.0\",\"info\":{\"init_bpm\":120,\"mode_hint\":\"custom-mode\"},\"sound_channels\":["
        + "{\"name\":\"same.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":240}]},"
        + "{\"name\":\"same.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":240}]},"
        + "{\"name\":\"release.wav\",\"notes\":[{\"x\":1,\"y\":240,\"up\":true}]},"
        + "{\"name\":\"short.wav\",\"notes\":[{\"x\":2,\"y\":120}]},"
        + "{\"name\":\"short.wav\",\"notes\":[{\"x\":2,\"y\":120}]}],"
        + "\"key_channels\":[{\"notes\":[{\"y\":480}]}],\"mine_channels\":[{\"notes\":[{\"y\":480}]}],\"scroll_events\":[{\"y\":0,\"rate\":2}]}";

}

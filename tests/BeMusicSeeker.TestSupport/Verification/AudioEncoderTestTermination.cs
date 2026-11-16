using System.Text;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Verification;

/// <summary>実cleanup後に残った音声ownerの観測結果です。通常の例外やhandle破棄失敗とは区別します。</summary>
internal enum AudioEncoderCleanupObservation
{
    /// <summary>未開始、または停止と子終端を確認済みです。</summary>
    NoUnconfirmedOwner,
    /// <summary>取得済みnative encoderの停止を確認できません。</summary>
    NativeStopUnconfirmed,
    /// <summary>native停止後も子の実exitを取得できないか、STILL_ACTIVEです。</summary>
    ChildExitUnconfirmed,
    /// <summary>writerの最終解放要求がfalseを返し、owner解放を確認できません。</summary>
    WriterOwnerUnreleased
}

/// <summary>未解放の音声ownerを次caseへ渡さず、診断と媒体を残して専有test processを非成功終了します。</summary>
internal static class AudioEncoderTestTermination
{
    /// <summary>実未解放観測だけを非正常終了へ接続します。通常失敗は呼出元の回収と再送出へ戻します。</summary>
    internal static void Complete(AudioEncoderCleanupObservation observation, TestWorkspace workspace,
        ProcessDeadline deadline, Exception? primaryFailure, string lastObservation)
    {
        if (observation == AudioEncoderCleanupObservation.NoUnconfirmedOwner) { return; }
        if (observation is not (AudioEncoderCleanupObservation.NativeStopUnconfirmed
            or AudioEncoderCleanupObservation.ChildExitUnconfirmed or AudioEncoderCleanupObservation.WriterOwnerUnreleased))
        { throw new ArgumentOutOfRangeException(nameof(observation)); }
        workspace.PreserveArtifacts();
        Exception failure = primaryFailure ?? new InvalidOperationException("音声ownerの解放を確認できません。");
        string Describe()
        {
            StringBuilder text = new StringBuilder()
                .AppendLine("音声testの未解放ownerを後続caseへ渡さず、専有processを非正常終了します。")
                .AppendLine("observation=" + observation)
                .AppendLine("last=" + lastObservation)
                .AppendLine("suite-cutoff=" + deadline.Export())
                .AppendLine("workspace=" + workspace.RootDirectory)
                .AppendLine(failure.ToString());
            if (failure.Data["AudioSmoke.CleanupFailures"] is List<Exception> cleanupFailures)
            {
                foreach (Exception cleanup in cleanupFailures) { text.AppendLine("cleanup: " + cleanup); }
            }
            return text.ToString();
        }
        void AddDiagnosticFailure(Exception diagnosticFailure)
        {
            if (failure.Data["AudioSmoke.CleanupFailures"] is not List<Exception> failures)
            { failures = []; failure.Data["AudioSmoke.CleanupFailures"] = failures; }
            failures.Add(diagnosticFailure);
        }
        try { File.WriteAllText(workspace.GetPath("audio-owner-unreleased.txt"), Describe()); }
        catch (Exception diagnosticFailure) { AddDiagnosticFailure(diagnosticFailure); }
        try { Console.Error.WriteLine(Describe()); }
        catch (Exception diagnosticFailure) { AddDiagnosticFailure(diagnosticFailure); }
        Environment.FailFast(Describe(), failure);
    }
}

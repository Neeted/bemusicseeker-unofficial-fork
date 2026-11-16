#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Runtime;

/// <summary>明示test sessionだけで、外部shellへの要求をprocessを起動せず記録する窓口です。</summary>
/// <remarks>
/// 検証起動が所有Job外のprocess（既定ブラウザ等）を起動しないための差替えです。通常起動は
/// <see cref="ExternalShellGatewayPolicy.Current"/>を使い、この型を経由しません。
/// URL以外の操作は成功を装わず、失敗として返します。
/// </remarks>
internal sealed class ApplicationScenarioExternalShell : IExternalShellGateway
{
    private readonly object gate = new();
    private readonly List<ScenarioExternalShellRecord> records = [];

    /// <summary>受付順に記録した要求の変更不能な複製を返します。</summary>
    internal ImmutableArray<ScenarioExternalShellRecord> Capture()
    {
        lock (gate) { return [.. records]; }
    }

    /// <inheritdoc />
    public void Open(ExternalShellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Record(new("Open", request.Kind.ToString(), request.Target));
    }

    /// <inheritdoc />
    public ExplorerOpenResult OpenFileAndSelect(string filePath)
    {
        Record(new("OpenFileAndSelect", null, filePath));
        return Failure(filePath);
    }

    /// <inheritdoc />
    public ExplorerOpenResult OpenDirectory(string directoryPath)
    {
        Record(new("OpenDirectory", null, directoryPath));
        return Failure(directoryPath);
    }

    /// <inheritdoc />
    public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
    {
        Record(new("OpenDirectoryWithExplorerProcess", null, directoryPath));
        failureReason = "The test session does not start external processes.";
        return false;
    }

    private void Record(ScenarioExternalShellRecord record)
    {
        lock (gate) { records.Add(record); }
    }

    private static ExplorerOpenResult Failure(string path) => new()
    {
        Kind = ExplorerOpenResultKind.Failed,
        RequestedPath = path,
        FailureReason = "The test session does not start external processes."
    };
}

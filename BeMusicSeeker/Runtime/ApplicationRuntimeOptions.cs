#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Runtime;

/// <summary>起動境界で捕捉した配置と実行条件を、同じアプリ構成へ変更不能な値として渡します。</summary>
internal sealed class ApplicationRuntimeOptions
{
    /// <summary>確定した実行条件を保持し、再起動引数を呼出元の配列から分離します。</summary>
    /// <param name="paths">実行体と可変データの配置。</param>
    /// <param name="databaseLoadOptions">DBの読取り方式。</param>
    /// <param name="testSession">明示的な検証起動の識別情報。通常起動はnull。</param>
    /// <param name="updateManifestUrl">優先順位を解決した更新URL。通常CLIの妥当性は既存の更新確認境界で検査します。</param>
    /// <param name="restartArguments">実行ファイル名を除く、引用前の再起動引数列。</param>
    internal ApplicationRuntimeOptions(
        ApplicationPathSnapshot paths,
        BmsDatabaseLoadOptions databaseLoadOptions,
        ApplicationTestSession? testSession,
        string updateManifestUrl,
        IEnumerable<string> restartArguments)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        DatabaseLoadOptions = databaseLoadOptions ?? throw new ArgumentNullException(nameof(databaseLoadOptions));
        TestSession = testSession;
        UpdateManifestUrl = updateManifestUrl ?? throw new ArgumentNullException(nameof(updateManifestUrl));
        ArgumentNullException.ThrowIfNull(restartArguments);
        RestartArguments = Array.AsReadOnly(restartArguments.ToArray());
    }

    /// <summary>実行体とプロファイルの確定済み配置を取得します。</summary>
    internal ApplicationPathSnapshot Paths { get; }

    /// <summary>起動時に捕捉したDB読取り方式を取得します。</summary>
    internal BmsDatabaseLoadOptions DatabaseLoadOptions { get; }

    /// <summary>明示的な検証起動の識別情報を取得します。通常起動ではnullです。</summary>
    internal ApplicationTestSession? TestSession { get; }

    /// <summary>優先順位を解決した更新URLを取得します。取得時に環境やCLIを再読しません。</summary>
    internal string UpdateManifestUrl { get; }

    /// <summary>実行ファイル名を除く、変更不能な再起動引数列を取得します。</summary>
    internal IReadOnlyList<string> RestartArguments { get; }

    /// <summary>通常起動のCLIと環境を捕捉し、更新URL未指定時だけ従来の既定値を採用します。</summary>
    internal static ApplicationRuntimeOptions Capture(ApplicationPathSnapshot paths)
        => new(paths, BmsDatabaseLoadOptions.CaptureEnvironment(), null,
            string.IsNullOrWhiteSpace(CommandLineSwitches.UpdateManifestUrl)
                ? UpdateCheckService.DefaultManifestUrl : CommandLineSwitches.UpdateManifestUrl,
            Environment.GetCommandLineArgs().Skip(1));
}

/// <summary>検証起動で共有する識別情報です。プロファイルの排他キーや操作結果の代わりには使いません。</summary>
internal sealed class ApplicationTestSession
{
    /// <summary>起動入口で検証済みのmanifestと実行・通信の識別子を保持します。</summary>
    internal ApplicationTestSession(string manifestPath, string runId, string caseId, string controlPipeName)
    {
        ManifestPath = manifestPath ?? throw new ArgumentNullException(nameof(manifestPath));
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        CaseId = caseId ?? throw new ArgumentNullException(nameof(caseId));
        ControlPipeName = controlPipeName ?? throw new ArgumentNullException(nameof(controlPipeName));
    }

    /// <summary>再起動後も読むmanifestの絶対パスを取得します。</summary>
    internal string ManifestPath { get; }

    /// <summary>診断用の実行識別子を取得します。</summary>
    internal string RunId { get; }

    /// <summary>診断用のケース識別子を取得します。</summary>
    internal string CaseId { get; }

    /// <summary>同じ検証実行で使う制御pipeの名前を取得します。</summary>
    internal string ControlPipeName { get; }
}

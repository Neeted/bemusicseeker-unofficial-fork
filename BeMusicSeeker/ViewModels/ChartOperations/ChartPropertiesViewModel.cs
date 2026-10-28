using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Livet;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>一回の読取り結果を現言語で整形し、閉鎖後の通知と結果反映を終了します。</summary>
internal sealed class ChartPropertiesViewModel : ViewModel
{
    private readonly Func<ChartResourceDiagnostics> read;
    private readonly ChartFileKind kind;
    private readonly ChartResourcePlacementBasis placementBasis;
    private ChartResourceDiagnostics diagnostics;
    private bool failed;
    private Task loadTask;
    private bool closed;

    /// <summary>捕捉された選択譜面の実パスと読取り処理で表示と言語購読を準備します。構築時には読まず、購読はCloseで終了します。</summary>
    internal ChartPropertiesViewModel(ChartFile chart, Func<ChartResourceDiagnostics> read = null, ChartResourcePlacement placement = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        kind = chart.Kind;
        Path = chart.Path;
        Title = chart.Title;
        Artist = chart.Artist;
        placement ??= new ChartResourcePlacement(ChartResourcePlacementBasis.Current, System.IO.Path.GetDirectoryName(chart.Path));
        VerificationPath = placement.Directory;
        placementBasis = placement.Basis;
        ChartResourcePlacement capturedPlacement = placement;
        this.read = read ?? (() => new ChartResourceDiagnosticsService().Read(chart.Path, chart.Kind, capturedPlacement));
        ResourceService.Current.PropertyChanged += ResourceServicePropertyChanged;
    }

    /// <summary>プロパティを開く時点の配置を機能側で捕捉します。通常所持は実配置を維持します。</summary>
    internal static ChartResourcePlacement CapturePlacement(ChartOperationTarget target)
        => target.IsPending ? target.PackageEntry?.ResourcePlacement ?? ChartResourcePlacement.Select(target.Chart, null, null) : null;

    /// <summary>一件のローカル実ファイルを対象にできるかを判定します。譜面本体は元の実ファイルから読みます。</summary>
    internal static ChartOperationTarget ResolveTarget(IReadOnlyList<ChartOperationTarget> targets)
        => targets?.Count == 1 && !targets[0].IsPlaylistMissing
            && !string.IsNullOrWhiteSpace(targets[0].Chart.Path)
            && LongPathFileSystem.FileExists(targets[0].Chart.Path) ? targets[0] : null;

    /// <summary>捕捉した譜面パスです。</summary>
    public string Path { get; }
    /// <summary>開いた時点で捕捉した資源の確認基準を現言語で表示します。</summary>
    public string VerificationBasis => placementBasis switch
    {
        ChartResourcePlacementBasis.Destination => Resources.ChartPropertiesConfiguredDestination,
        ChartResourcePlacementBasis.FirstCandidate => Resources.ChartPropertiesFirstCandidate,
        _ => Resources.ChartPropertiesCurrentPlacement
    };
    /// <summary>開いた時点で捕捉した資源の確認先です。</summary>
    public string VerificationPath { get; }
    /// <summary>読み取ったタイトルです。</summary>
    public string Title { get; private set; }
    /// <summary>読み取ったアーティストです。</summary>
    public string Artist { get; private set; }
    /// <summary>取得・解析失敗時だけ表示する文言です。初期・読取り中・成功時は空です。</summary>
    public string Status { get; private set; } = string.Empty;
    /// <summary>取得済みの不足・確認不能を現言語で整形した、コピー可能な一覧です。</summary>
    public string Issues { get; private set; } = string.Empty;
    /// <summary>定義元を保つ未使用リソース定義の一覧です。</summary>
    public string UnusedDefinitions { get; private set; } = string.Empty;
    /// <summary>名前空間ごとに重複を除いた未定義参照番号です。</summary>
    public string UndefinedReferences { get; private set; } = string.Empty;

    /// <summary>譜面形式に合った未使用定義の説明です。</summary>
    public string UnusedDescription => kind == ChartFileKind.Bmson ? Resources.ChartPropertiesBmsonUnusedDescription : Resources.ChartPropertiesUnusedDescription;
    /// <summary>譜面形式に合った未定義参照番号の説明です。</summary>
    public string UndefinedDescription => kind == ChartFileKind.Bmson ? Resources.ChartPropertiesBmsonUndefinedDescription : Resources.ChartPropertiesUndefinedDescription;

    /// <summary>背景読取りを一度だけ開始し、失敗を表示へ回収した同じTaskを返します。</summary>
    internal Task LoadAsync() => loadTask ??= LoadCoreAsync();

    /// <summary>言語購読と画面への反映を終了します。開始済みTaskは引き続き失敗を回収します。</summary>
    internal void Close()
    {
        closed = true;
        ResourceService.Current.PropertyChanged -= ResourceServicePropertyChanged;
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            ChartResourceDiagnostics result = await Task.Run(read);
            if (closed)
            {
                return;
            }
            Title = result.Title;
            Artist = result.Artist;
            diagnostics = result;
            RaisePropertyChanged(nameof(Title));
            RaisePropertyChanged(nameof(Artist));
            RefreshLocalizedText();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try
            {
                NLogWrapper.FileLogger?.Error(exception, "Chart properties read failed: path={0}", Path);
            }
            catch { /* 純粋な診断出力の失敗で、元の読取り失敗の終端や表示を変えません。 */ }
            if (!closed)
            {
                failed = true;
                RefreshLocalizedText();
            }
        }
    }

    private void ResourceServicePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (!closed && e.PropertyName == nameof(ResourceService.Resources))
        {
            RefreshLocalizedText();
        }
    }

    private void RefreshLocalizedText()
    {
        // 初期・読取り中を成功した空一覧へ読み替えず、取得済みの事実だけを再整形します。
        if (diagnostics is not null)
        {
            Issues = JoinOrNone(diagnostics.Issues.Select(issue =>
                FormatDefinition(issue.Definition) + " (" + FormatReason(issue.Reason) + ")"));
            UnusedDefinitions = JoinOrNone(diagnostics.UnusedDefinitions.Select(FormatDefinition));
            UndefinedReferences = JoinOrNone(diagnostics.UndefinedReferences.Select(number => FormatNumber(number.Source, number.Id)));
        }
        Status = failed ? Resources.ChartPropertiesFailed : string.Empty;
        RaisePropertyChanged(nameof(VerificationBasis));
        RaisePropertyChanged(nameof(Issues));
        RaisePropertyChanged(nameof(UnusedDefinitions));
        RaisePropertyChanged(nameof(UndefinedReferences));
        RaisePropertyChanged(nameof(UnusedDescription));
        RaisePropertyChanged(nameof(UndefinedDescription));
        RaisePropertyChanged(nameof(Status));
    }

    private static string JoinOrNone(IEnumerable<string> lines)
    {
        string text = string.Join(Environment.NewLine, lines);
        return text.Length == 0 ? Resources.None : text;
    }

    private static string FormatDefinition(ChartResourceDefinition definition)
        => FormatNumber(definition.Source, definition.Id) + " " + definition.Reference.RawPath;

    private static string FormatNumber(string source, string id)
        => string.IsNullOrEmpty(id) ? source
            : source.StartsWith("#", StringComparison.Ordinal) ? source + id : source + " (" + id + ")";

    private static string FormatReason(ChartResourceDiagnosticReason reason) => reason switch
    {
        ChartResourceDiagnosticReason.Missing => Resources.ChartPropertiesMissing,
        ChartResourceDiagnosticReason.Unverifiable => Resources.ChartPropertiesUnverifiable,
        ChartResourceDiagnosticReason.InvalidPath => Resources.ChartPropertiesInvalidPath,
        ChartResourceDiagnosticReason.Cp932DecodeUnsupported => Resources.ChartPropertiesCp932Unsupported,
        _ => Resources.ChartPropertiesUnsupported
    };
}

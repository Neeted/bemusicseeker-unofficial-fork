using System;
using System.Collections.Immutable;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

/// <summary>header保存が所有する属性です。指定しない属性はwriter内の現在値を保持します。</summary>
[Flags]
internal enum PlaylistHeaderFields
{
    None = 0, FolderOrder = 1, FolderSortKey = 2, FolderSortAscending = 4, IgnoreFolderOutput = 8,
    EntryType = 16, Name = 32, Symbol = 64, PageUrl = 128, HeaderUrl = 256, DataUrl = 512,
    IsExternalSync = 1024, OutputDirectory = 2048, CustomFolderOutputBaseName = 4096,
    IsRootFolder = 8192, CompatPrefix = 16384
}

/// <summary>未保存のheader編集要求です。entry値や全表copyを保持せず、編集対象だけを同じwriterで適用します。</summary>
internal sealed record PlaylistHeaderEdit
{
    internal PlaylistHeaderFields Fields { get; init; }
    internal ImmutableArray<string> FolderOrder { get; init; }
    internal LR2SongDBExtended.playlist.CustomFolderSortType FolderSortKey { get; init; }
    internal bool FolderSortAscending { get; init; }
    internal LR2SongDBExtended.playlist.CustomFolderType IgnoreFolderOutput { get; init; }
    internal LR2SongDBExtended.playlist.EntryUnitType EntryType { get; init; }
    internal string Name { get; init; }
    internal string Symbol { get; init; }
    internal Uri PageUrl { get; init; }
    internal Uri HeaderUrl { get; init; }
    internal Uri DataUrl { get; init; }
    internal bool IsExternalSync { get; init; }
    internal string OutputDirectory { get; init; }
    internal string CustomFolderOutputBaseName { get; init; }
    internal bool IsRootFolder { get; init; }
    internal string CompatPrefix { get; init; }
}

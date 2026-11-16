using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>現在の重複検索結果だけで使う表示対象です。見出しではなく所属フォルダを値で捕捉します。</summary>
internal sealed class DuplicateViewContext
{
    /// <summary>全件・グループ所属・フォルダ配下を区別します。</summary>
    internal DuplicateViewContextKind Kind { get; }

    /// <summary>捕捉した所属または配下のフォルダです。空の所属は未解決を表し、全件へ広げません。</summary>
    internal string Value { get; }

    private DuplicateViewContext(DuplicateViewContextKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>利用者が明示的に選ぶ重複ルートの全件表示を表します。</summary>
    internal static DuplicateViewContext ForRoot() => new(DuplicateViewContextKind.Root, string.Empty);

    /// <summary>所属フォルダによるグループ入力を捕捉します。空や消失した入力は未解決のまま扱います。</summary>
    internal static DuplicateViewContext ForGroup(string memberFolder)
        => new(DuplicateViewContextKind.GroupFolder, memberFolder);

    /// <summary>グループの一所属を文字列として捕捉し、後のFoldersやHeaderの変更から切り離します。</summary>
    internal static DuplicateViewContext CaptureGroup(DuplicateGroup group)
        => ForGroup(group?.Folders?.FirstOrDefault(folder => !string.IsNullOrWhiteSpace(folder)));

    /// <summary>従来のフォルダ配下表示を表し、同じパスのグループ全体とは区別します。</summary>
    internal static DuplicateViewContext ForFolder(string folderPath)
        => new(DuplicateViewContextKind.FolderPath, folderPath);

    /// <summary>現在の結果内で一意な所属だけを解決します。未解決・曖昧な所属を別群や全件へ読み替えません。</summary>
    internal DuplicateGroup ResolveGroup(IEnumerable<DuplicateGroup> groups)
    {
        if (Kind != DuplicateViewContextKind.GroupFolder || string.IsNullOrWhiteSpace(Value)) { return null; }
        DuplicateGroup resolved = null;
        foreach (DuplicateGroup group in groups ?? [])
        {
            if (group?.Folders?.Contains(Value, StringComparer.OrdinalIgnoreCase) != true) { continue; }
            if (resolved != null) { return null; }
            resolved = group;
        }
        return resolved;
    }
}

/// <summary>明示ルートと、所属・配下という異なる検索範囲を区別します。</summary>
internal enum DuplicateViewContextKind
{
    Root,
    GroupFolder,
    FolderPath
}

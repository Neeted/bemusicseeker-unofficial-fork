using System;
using System.IO;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>JSON保存先の拒否条件をWindow・DBなしでまとめて確認します。</summary>
[TestClass]
public sealed class PlaylistJsonExportDialogViewModelTests
{
    /// <summary>未入力・case・相対成分の同一先は拒否し、別名・別ディレクトリ同名は許可します。ファイルの存在で判定は変わりません。</summary>
    [DataTestMethod]
    [DataRow("", "data.json", false)]
    [DataRow("header.json", " ", false)]
    [DataRow("header.json", "header.json", false)]
    [DataRow("header.json", "HEADER.JSON", false)]
    [DataRow("header.json", "./header.json", false)]
    [DataRow("header.json", "child/../header.json", false)]
    [DataRow("header.json", "data.json", true)]
    [DataRow("one/same.json", "two/same.json", true)]
    public void SavePaths_RequireTwoDistinctNormalizedDestinations(string header, string data, bool accepted)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistJsonExportDialogViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string headerPath = string.IsNullOrWhiteSpace(header) ? header : Path.Combine(directory, header);
            string dataPath = string.IsNullOrWhiteSpace(data) ? data : Path.Combine(directory, data);
            using var input = new PlaylistJsonExportDialogViewModel { HeaderPath = headerPath, DataPath = dataPath };
            Assert.AreEqual(accepted, input.CanExport);
            Assert.AreEqual(accepted, string.IsNullOrEmpty(input.ValidationMessage));
            // 同じ判定を既存ファイルでも確認し、存在確認や自動採番に依存する誤実装を検出します。
            foreach (string path in new[] { headerPath, dataPath })
            {
                if (string.IsNullOrWhiteSpace(path)) { continue; }
                string normalized = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(normalized) ?? throw new InvalidOperationException());
                File.WriteAllText(normalized, "existing");
            }
            Assert.AreEqual(accepted, input.CanExport);
            Assert.AreEqual(accepted, string.IsNullOrEmpty(input.ValidationMessage));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

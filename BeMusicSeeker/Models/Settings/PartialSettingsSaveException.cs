using System;

namespace BeMusicSeeker.Models;

/// <summary>user.config保存後にLR2 XMLが未保存となった事実と、修復対象のパス・元原因を通知へ渡します。</summary>
internal sealed class PartialSettingsSaveException(string filePath, Exception cause)
    : Exception("Application settings were saved, but LR2 configuration could not be saved: " + filePath, cause)
{
    /// <summary>未保存のLR2設定ファイルです。</summary>
    internal string FilePath { get; } = filePath;
}

using System;
using System.Configuration;

namespace BeMusicSeeker.Properties;

/// <summary>ポータブル設定の操作と対象ファイルを示し、元の失敗を保持します。</summary>
/// <remarks>失敗報告に必要なファイルパス、操作、原因例外を保持するため、標準の簡略コンストラクターは提供しません。</remarks>
internal sealed class PortableSettingsException : ConfigurationErrorsException
{
    /// <summary>Captures a failure at the file-owning boundary.</summary>
    internal PortableSettingsException(string filePath, string operation, Exception cause)
        : base($"Portable settings {operation} failed: {filePath}{Environment.NewLine}{cause.Message}", cause)
    {
        FilePath = filePath;
        Operation = operation;
    }

    /// <summary>The configuration file involved in the failed operation.</summary>
    internal string FilePath { get; }

    /// <summary>The failed Read, Save, Create, Quarantine or Migrate operation.</summary>
    internal string Operation { get; }
}

using System;
using System.Configuration;
using System.Diagnostics.CodeAnalysis;

namespace BeMusicSeeker.Properties;

/// <summary>Identifies the portable settings operation and file while retaining the original failure.</summary>
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "設定の復旧判断と失敗報告にはファイルパス・操作・原因例外が必須であり、それらを欠く標準コンストラクターは提供しない。")]
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

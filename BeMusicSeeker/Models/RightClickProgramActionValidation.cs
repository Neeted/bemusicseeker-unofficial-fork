using System;
using System.IO;

namespace BeMusicSeeker.Models;

/// <summary>
/// プログラム action の入力検証で検出した原因です。
/// </summary>
internal enum RightClickProgramActionValidationErrorKind
{
    None,
    NameRequired,
    ExecutablePathRequired,
    ExecutablePathNotAbsolute,
    ArgumentTemplateRequired,
    ArgumentTemplateMissingFilePath,
    ArgumentTemplateUnknownPlaceholder,
    ArgumentTemplateUnbalancedPlaceholder,
    ArgumentTemplateUnbalancedDoubleQuote
}

/// <summary>
/// プログラム action の欄別検証を保存時と編集中で共有します。
/// </summary>
internal static class RightClickProgramActionValidator
{
    /// <summary>
    /// 名前を検証します。
    /// </summary>
    internal static RightClickProgramActionValidationErrorKind ValidateName(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? RightClickProgramActionValidationErrorKind.NameRequired
            : RightClickProgramActionValidationErrorKind.None;
    }

    /// <summary>
    /// 実行ファイルのパスを検証します。
    /// </summary>
    internal static RightClickProgramActionValidationErrorKind ValidateExecutablePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RightClickProgramActionValidationErrorKind.ExecutablePathRequired;
        }

        return Path.IsPathFullyQualified(value)
            ? RightClickProgramActionValidationErrorKind.None
            : RightClickProgramActionValidationErrorKind.ExecutablePathNotAbsolute;
    }

    /// <summary>
    /// Windows の引数分解規則を使って引数テンプレートを検証します。
    /// </summary>
    internal static RightClickProgramActionValidationErrorKind ValidateArgumentTemplate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RightClickProgramActionValidationErrorKind.ArgumentTemplateRequired;
        }

        if (ExternalProgramArgumentTemplate.TryParse(
            value,
            out _,
            out ExternalProgramArgumentTemplateErrorKind errorKind,
            out _))
        {
            return RightClickProgramActionValidationErrorKind.None;
        }

        return errorKind switch
        {
            ExternalProgramArgumentTemplateErrorKind.Required
                => RightClickProgramActionValidationErrorKind.ArgumentTemplateRequired,
            ExternalProgramArgumentTemplateErrorKind.MissingFilePathPlaceholder
                => RightClickProgramActionValidationErrorKind.ArgumentTemplateMissingFilePath,
            ExternalProgramArgumentTemplateErrorKind.UnknownPlaceholder
                => RightClickProgramActionValidationErrorKind.ArgumentTemplateUnknownPlaceholder,
            ExternalProgramArgumentTemplateErrorKind.UnbalancedPlaceholder
                => RightClickProgramActionValidationErrorKind.ArgumentTemplateUnbalancedPlaceholder,
            ExternalProgramArgumentTemplateErrorKind.UnbalancedDoubleQuote
                => RightClickProgramActionValidationErrorKind.ArgumentTemplateUnbalancedDoubleQuote,
            ExternalProgramArgumentTemplateErrorKind.None
                => throw new InvalidOperationException("引数テンプレートの検証失敗原因がありません。"),
            _ => throw new ArgumentOutOfRangeException(nameof(errorKind), errorKind, "未知の引数テンプレート検証原因です。")
        };
    }
}

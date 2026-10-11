using System.Windows;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Locks the pure result-normalization contract without creating or showing a dialog window.
/// </summary>
[TestClass]
public sealed class ThemedMessageBoxTests
{
    /// <summary>各ボタン集合と全旧結果を組み合わせ、不成立の既定応答をCancel/Noへ正規化して未承認の肯定を防ぎます。</summary>
    [TestMethod]
    public void NormalizeDefaultResult_CoversEveryButtonAndResultCombination()
    {
        MessageBoxResult[] results =
        [
            MessageBoxResult.None,
            MessageBoxResult.OK,
            MessageBoxResult.Cancel,
            MessageBoxResult.Yes,
            MessageBoxResult.No,
            MessageBoxResult.Abort,
            MessageBoxResult.Retry,
            MessageBoxResult.Ignore
        ];

        foreach (MessageBoxResult result in results)
        {
            Assert.AreEqual(
                MessageBoxResult.OK,
                ThemedMessageBox.NormalizeDefaultResult(MessageBoxButton.OK, result),
                $"Unexpected OK normalization for {result}.");
            Assert.AreEqual(
                result is MessageBoxResult.OK or MessageBoxResult.Cancel
                    ? result
                    : MessageBoxResult.Cancel,
                ThemedMessageBox.NormalizeDefaultResult(MessageBoxButton.OKCancel, result),
                $"Unexpected OKCancel normalization for {result}.");
            Assert.AreEqual(
                result is MessageBoxResult.Yes or MessageBoxResult.No
                    ? result
                    : MessageBoxResult.No,
                ThemedMessageBox.NormalizeDefaultResult(MessageBoxButton.YesNo, result),
                $"Unexpected YesNo normalization for {result}.");
            Assert.AreEqual(
                result is MessageBoxResult.Yes or MessageBoxResult.No or MessageBoxResult.Cancel
                    ? result
                    : MessageBoxResult.Cancel,
                ThemedMessageBox.NormalizeDefaultResult(MessageBoxButton.YesNoCancel, result),
                $"Unexpected YesNoCancel normalization for {result}.");
        }

        Assert.AreEqual(
            MessageBoxResult.None,
            ThemedMessageBox.NormalizeDefaultResult((MessageBoxButton)999, MessageBoxResult.OK));
    }

    /// <summary>旧結果のOKとYesだけを肯定と扱い、取消・閉鎖・Retry等を操作許可へ誤変換しません。</summary>
    [TestMethod]
    public void IsAffirmative_CoversEveryLegacyMessageBoxResult()
    {
        Assert.IsFalse(ThemedMessageBox.IsAffirmative(MessageBoxResult.None));
        Assert.IsTrue(ThemedMessageBox.IsAffirmative(MessageBoxResult.OK));
        Assert.IsFalse(ThemedMessageBox.IsAffirmative(MessageBoxResult.Cancel));
        Assert.IsTrue(ThemedMessageBox.IsAffirmative(MessageBoxResult.Yes));
        Assert.IsFalse(ThemedMessageBox.IsAffirmative(MessageBoxResult.No));
        Assert.IsFalse(ThemedMessageBox.IsAffirmative(MessageBoxResult.Abort));
        Assert.IsFalse(ThemedMessageBox.IsAffirmative(MessageBoxResult.Retry));
        Assert.IsFalse(ThemedMessageBox.IsAffirmative(MessageBoxResult.Ignore));
    }

    /// <summary>旧結果の肯定・否定・取消をtrue/false/nullへ分け、取消や閉鎖を明示否定と取り違えません。</summary>
    [TestMethod]
    public void ToConfirmationResponse_CoversEveryLegacyMessageBoxResult()
    {
        Assert.IsNull(ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.None));
        Assert.AreEqual(true, ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.OK));
        Assert.IsNull(ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.Cancel));
        Assert.AreEqual(true, ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.Yes));
        Assert.AreEqual(false, ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.No));
        Assert.IsNull(ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.Abort));
        Assert.IsNull(ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.Retry));
        Assert.IsNull(ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.Ignore));
    }
}

using System;

namespace BeMusicSeeker.Models;

public partial class BMSTableEntry
{
    private Uri runtimeUrlCompletion;

    private Uri runtimeUrlDiffCompletion;

    /// <summary>
    /// 自動補完で解決したランタイム専用の URL1 値を取得します。
    /// DB や JSON には保存せず、画面表示と保存時 materialize の判断だけに使います。
    /// </summary>
    public Uri RuntimeUrlCompletion => runtimeUrlCompletion;

    /// <summary>
    /// 自動補完で解決したランタイム専用の URL2 値を取得します。
    /// DB や JSON には保存せず、画面表示と保存時 materialize の判断だけに使います。
    /// </summary>
    public Uri RuntimeUrlDiffCompletion => runtimeUrlDiffCompletion;

    /// <summary>
    /// 画面表示や保存時に優先して使う URL1 を取得します。
    /// ランタイム補完がある場合はそれを返し、なければ永続化済み URL を返します。
    /// </summary>
    public Uri EffectiveUrl => runtimeUrlCompletion ?? Url;

    /// <summary>
    /// 画面表示や保存時に優先して使う URL2 を取得します。
    /// ランタイム補完がある場合はそれを返し、なければ永続化済み URL を返します。
    /// </summary>
    public Uri EffectiveUrlDiff => runtimeUrlDiffCompletion ?? Url_diff;

    /// <summary>
    /// 補完候補に基づいてランタイム専用 URL を更新します。
    /// 永続化済み URL の有無と上書き設定を見て、表示上だけ補完値を差し込みます。
    /// </summary>
    /// <param name="completedUrl">補完候補の URL1。候補なしなら null。</param>
    /// <param name="completedUrlDiff">補完候補の URL2。候補なしなら null。</param>
    /// <param name="overwriteExisting">既存の永続化済み URL を表示上書きしてよいかどうか。</param>
    /// <returns>ランタイム補完状態が変化した場合は true。</returns>
    internal bool ApplyRuntimeUrlCompletion(Uri completedUrl, Uri completedUrlDiff, bool overwriteExisting)
    {
        bool urlChanged = ApplyRuntimeUrlCompletionField(ref runtimeUrlCompletion, completedUrl, Url, overwriteExisting);
        bool urlDiffChanged = ApplyRuntimeUrlCompletionField(ref runtimeUrlDiffCompletion, completedUrlDiff, Url_diff, overwriteExisting);
        return urlChanged || urlDiffChanged;
    }

    /// <summary>
    /// URL1 のランタイム補完値だけをクリアします。
    /// ユーザーが URL1 を明示編集したときに補完より手入力を優先させるために使います。
    /// </summary>
    /// <returns>ランタイム補完値が消えた場合は true。</returns>
    internal bool ClearRuntimeUrlCompletion()
    {
        if (runtimeUrlCompletion == null)
        {
            return false;
        }
        runtimeUrlCompletion = null;
        return true;
    }

    /// <summary>
    /// URL2 のランタイム補完値だけをクリアします。
    /// ユーザーが URL2 を明示編集したときに補完より手入力を優先させるために使います。
    /// </summary>
    /// <returns>ランタイム補完値が消えた場合は true。</returns>
    internal bool ClearRuntimeUrlDiffCompletion()
    {
        if (runtimeUrlDiffCompletion == null)
        {
            return false;
        }
        runtimeUrlDiffCompletion = null;
        return true;
    }

    /// <summary>
    /// URL1/URL2 のランタイム補完値をまとめてクリアします。
    /// 補完機能を無効化した場合や、補完候補が消えた場合の後始末に使います。
    /// </summary>
    /// <returns>いずれかのランタイム補完値が変化した場合は true。</returns>
    internal bool ClearRuntimeUrlCompletions()
    {
        bool urlChanged = ClearRuntimeUrlCompletion();
        bool urlDiffChanged = ClearRuntimeUrlDiffCompletion();
        return urlChanged || urlDiffChanged;
    }

    /// <summary>
    /// 現在の effective URL を永続化対象の URL フィールドへ反映します。
    /// ローカルプレイリストで行保存が発生したときだけ、表示上の補完結果を DB 保存用の値へ昇格させます。
    /// </summary>
    internal void MaterializeEffectiveUrlsIntoPersistedValues()
    {
        Uri effectiveUrl = EffectiveUrl;
        Uri effectiveUrlDiff = EffectiveUrlDiff;
        deferredUrlRaw = null;
        deferredUrlDiffRaw = null;
        _url = effectiveUrl;
        _urlDiff = effectiveUrlDiff;
        ClearRuntimeUrlCompletions();
    }

    private static bool ApplyRuntimeUrlCompletionField(ref Uri runtimeField, Uri completedValue, Uri persistedValue, bool overwriteExisting)
    {
        Uri normalizedCompletedValue = NormalizeAbsoluteUri(completedValue);
        Uri nextValue = null;
        if (normalizedCompletedValue != null && (overwriteExisting || !HasAbsoluteUri(persistedValue)))
        {
            nextValue = normalizedCompletedValue;
        }
        if (UriEquals(runtimeField, nextValue))
        {
            return false;
        }
        runtimeField = nextValue;
        return true;
    }

    private static Uri NormalizeAbsoluteUri(Uri value)
    {
        if (value == null || !value.IsAbsoluteUri)
        {
            return null;
        }
        return value;
    }

    private static bool HasAbsoluteUri(Uri value)
    {
        return value != null && value.IsAbsoluteUri;
    }

    private static bool UriEquals(Uri left, Uri right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null)
        {
            return false;
        }
        return Uri.Compare(left, right, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }
}

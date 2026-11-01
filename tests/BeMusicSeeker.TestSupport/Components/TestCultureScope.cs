using System.Globalization;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>呼出しcontextの現在cultureとUI cultureを保存・復元する専有scopeだけを共用し、Appの辞書初期化は旧AssemblyInitializeへ残します。</summary>
internal static class TestCultureScope
{
    /// <summary>日本語のcultureを適用し、Disposeで保存済みCurrentCultureとCurrentUICultureへ戻すscopeを返します。呼出元が同じ検証寿命内でDisposeします。</summary>
    public static IDisposable UseJapaneseCulture()
    {
        return new CultureScope(CultureInfo.GetCultureInfo("ja-JP"));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previousCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        internal CultureScope(CultureInfo culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

}

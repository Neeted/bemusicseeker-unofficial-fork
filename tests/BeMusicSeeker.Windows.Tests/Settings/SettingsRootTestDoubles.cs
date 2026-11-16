using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Tests;

/// <summary>複数の設定テストで同じ保存回数と保存終端を観測します。</summary>
internal sealed class CountingSettingsEditSession : ISettingsEditSession
{
    public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
    {
        environment.Commit(Values);
        Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
        Save();
        Reload();
    }

    internal CountingSettingsEditSession(Settings values)
    {
        Values = values;
    }

    internal Action? SaveObserved { get; set; }

    internal Exception? SaveFailure { get; set; }

    internal bool BlockSave { get; set; }

    internal bool PersistOwnedValues { get; init; }

    internal ManualResetEventSlim SaveEntered { get; } = new(false);

    internal ManualResetEventSlim ReleaseSave { get; } = new(false);

    internal int SaveCount { get; private set; }

    public Settings Values { get; }

    public void Reload()
    {
        if (PersistOwnedValues) { Values.Reload(); }
    }

    public void Save()
    {
        SaveCount++;
        SaveObserved?.Invoke();
        if (SaveFailure != null)
        {
            throw SaveFailure;
        }
        if (BlockSave)
        {
            SaveEntered.Set();
            ReleaseSave.Wait();
        }
        if (PersistOwnedValues) { Values.Save(); }
    }
}

/// <summary>保存要求を実providerへ一回委譲し、元例外と保存結果をそのまま返す局所観測です。</summary>
internal sealed class InstallAdmissionObservingSettingsProvider : System.Configuration.SettingsProvider
{
    private readonly System.Configuration.SettingsProvider inner;
    private readonly Action observe;
    internal InstallAdmissionObservingSettingsProvider(System.Configuration.SettingsProvider inner, Action observe)
    {
        this.inner = inner; this.observe = observe;
        Initialize(nameof(InstallAdmissionObservingSettingsProvider), new System.Collections.Specialized.NameValueCollection());
    }
    public override string ApplicationName { get => inner.ApplicationName; set => inner.ApplicationName = value; }
    /// <summary>同期保存の要求回数です。初期表示や再表示からの追加保存を検出します。</summary>
    internal int SaveCount { get; private set; }
    /// <summary>保存要求に含まれた追加出力先の値を書込み順に記録します。途中の部分集合を書いていないことを確認する入力です。</summary>
    internal List<string> AdditionalOutputBaseDirsWrites { get; } = [];
    public override System.Configuration.SettingsPropertyValueCollection GetPropertyValues(System.Configuration.SettingsContext context, System.Configuration.SettingsPropertyCollection properties)
        => inner.GetPropertyValues(context, properties);
    public override void SetPropertyValues(System.Configuration.SettingsContext context, System.Configuration.SettingsPropertyValueCollection values)
    {
        SaveCount++;
        if (values[nameof(BeMusicSeeker.Properties.Settings.LR2CustomFolderAdditionalOutputBaseDirs)] is { } additional)
        { AdditionalOutputBaseDirsWrites.Add(additional.PropertyValue as string ?? string.Empty); }
        observe();
        try { inner.SetPropertyValues(context, values); }
        finally { observe(); }
    }
}

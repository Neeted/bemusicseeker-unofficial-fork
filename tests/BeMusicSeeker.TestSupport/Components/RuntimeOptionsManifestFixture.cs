using System.Text.Json.Nodes;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>解析入力だけを専有rootに用意し、profileへの書込みを検出して全入力を回収します。</summary>
internal sealed class RuntimeOptionsManifestFixture : IDisposable
{
    private readonly TestWorkspace? workspace;
    private static string ExecutablePath => Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの位置を取得できません。");
    /// <summary>通常は専有workspace、相対パス検査では呼出元所有workspaceのCWD内に、指定prefixとGUIDで入力rootを作ります。</summary>
    internal RuntimeOptionsManifestFixture(string? baseDirectory = null, string rootNamePrefix = "Bms runtime parser 検証 ")
    {
        workspace = baseDirectory is null ? TestWorkspace.Create(TestRunDirectory.RootDirectory) : null;
        Root = Path.Combine(baseDirectory ?? (workspace ?? throw new InvalidOperationException("専有領域がありません。")).RootDirectory, rootNamePrefix + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(Root); }
        catch
        {
            workspace?.Dispose();
            throw;
        }
    }

    /// <summary>このfixtureだけが所有し、終了時に回収するディレクトリです。</summary>
    internal string Root { get; }

    /// <summary>解析時に未作成のまま保つprofileの絶対パスです。</summary>
    internal string ProfileDirectory => Path.Combine(Root, "profile");

    /// <summary>専有するmanifest入力の絶対パスです。</summary>
    internal string ManifestPath => Path.Combine(Root, "session.json");

    /// <summary>既定配置の判定と不正値への置換に使う、必須fieldだけの入力を返します。</summary>
    internal JsonObject CreateDocument() => new()
    {
        ["schemaVersion"] = 1,
        ["profileDirectory"] = ProfileDirectory,
        ["runId"] = "run-id",
        ["caseId"] = "case-id",
        ["controlPipeName"] = "control-pipe"
    };

    /// <summary>新しい解析入力を専有ファイルへ書き込みます。</summary>
    internal void Write(JsonObject document) => File.WriteAllText(ManifestPath, document.ToJsonString());

    /// <summary>実起動と同じparserへmanifest指定と追加引数を渡します。</summary>
    internal ApplicationRuntimeOptions Capture(params string[] additionalArguments)
        => ApplicationRuntimeOptionsParser.Capture(ExecutablePath, new[] { "--test-session", ManifestPath }.Concat(additionalArguments));

    /// <summary>入力失敗後もmanifestのbytesと、profile未作成を含む専有rootの内容を保持することを確認します。</summary>
    internal void AssertRejected(string json, params string[] additionalArguments)
    {
        File.WriteAllText(ManifestPath, json);
        byte[] before = File.ReadAllBytes(ManifestPath);
        Assert.Throws<Exception>(() => Capture(additionalArguments), json);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(ManifestPath));
        AssertOnlyManifestExists();
    }

    /// <summary>解析の前後にprofileなどの出力が作られず、専有rootに入力だけが残ることを確認します。</summary>
    internal void AssertOnlyManifestExists()
    {
        Assert.IsFalse(Directory.Exists(ProfileDirectory), "解析だけでprofileを作成しません。");
        CollectionAssert.AreEqual(new[] { ManifestPath }, Directory.GetFileSystemEntries(Root));
    }

    /// <summary>自分で生成した専有rootと入力だけを回収します。</summary>
    public void Dispose()
    {
        if (workspace is not null) { workspace.Dispose(); }
        else { Directory.Delete(Root, recursive: true); }
    }
}

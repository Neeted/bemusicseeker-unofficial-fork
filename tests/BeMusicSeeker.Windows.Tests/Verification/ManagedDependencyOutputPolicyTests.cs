using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace BeMusicSeeker.Tests;

/// <summary>選択構成の実製品apphostに埋め込まれたWindows向け配布契約を確認します。</summary>
[TestClass]
public sealed class ManagedDependencyOutputPolicyTests
{
    /// <summary>ビルド済みapphostの埋込みマニフェストを読み、ソースだけを変更してPerMonitorV2が配布実体へ届かない誤りを検出します。</summary>
    [TestMethod]
    public void ApplicationExecutableEmbedsPerMonitorV2Manifest()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "fixtures", "application", "BeMusicSeeker.exe");
        nint module = LoadLibraryExForManifest(executable, 0, 0x22);
        Assert.AreNotEqual(nint.Zero, module, "構築済み実行ファイルをリソースとして開けません。");
        try
        {
            nint resource = FindResourceForManifest(module, 1, 24);
            Assert.AreNotEqual(nint.Zero, resource, "実行ファイルのマニフェストがありません。");
            uint size = SizeofResourceForManifest(module, resource);
            Assert.IsTrue(size > 0);
            nint bytes = LockResourceForManifest(LoadResourceForManifest(module, resource));
            Assert.AreNotEqual(nint.Zero, bytes);
            byte[] data = new byte[checked((int)size)];
            Marshal.Copy(bytes, data, 0, data.Length);
            string xml = Encoding.UTF8.GetString(data).TrimStart('\uFEFF').TrimEnd('\0');
            var manifest = XDocument.Parse(xml);
            XNamespace settings = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";
            Assert.AreEqual("PerMonitorV2", manifest.Descendants(settings + "dpiAwareness").Single().Value);
        }
        finally { Assert.IsTrue(FreeLibraryForManifest(module)); }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExForManifest(string path, nint file, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindResourceW", SetLastError = true)]
    private static extern nint FindResourceForManifest(nint module, nint name, nint type);
    [DllImport("kernel32.dll", EntryPoint = "SizeofResource", SetLastError = true)]
    private static extern uint SizeofResourceForManifest(nint module, nint resource);
    [DllImport("kernel32.dll", EntryPoint = "LoadResource", SetLastError = true)]
    private static extern nint LoadResourceForManifest(nint module, nint resource);
    [DllImport("kernel32.dll", EntryPoint = "LockResource")]
    private static extern nint LockResourceForManifest(nint resource);
    [DllImport("kernel32.dll", EntryPoint = "FreeLibrary")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibraryForManifest(nint module);
}

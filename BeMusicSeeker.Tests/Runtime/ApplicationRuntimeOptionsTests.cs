using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationRuntimeOptionsTests
{
    /// <summary>捕捉後の元配列の変更と公開コレクション経由の変更を拒み、再起動先の引数を固定します。</summary>
    [TestMethod]
    public void RestartArgumentsRemainCapturedAfterCallerChangesInput()
    {
        string manifest = Path.Combine(Path.GetTempPath(), "検証 session", "session.json");
        string[] arguments = ["--test-session", manifest, "--log-level=Warn"];
        var options = new ApplicationRuntimeOptions(
            ApplicationPathSnapshot.FromExecutablePath(Path.Combine(Path.GetTempPath(), "BeMusicSeeker.exe")),
            new BmsDatabaseLoadOptions(true, true, true),
            new ApplicationTestSession(manifest, "run", "case", "pipe"),
            "https://example.invalid/update.json", arguments);

        arguments[1] = "replacement.json";

        CollectionAssert.AreEqual(new[] { "--test-session", manifest, "--log-level=Warn" },
            new List<string>(options.RestartArguments));
        if (options.RestartArguments is IList<string> mutableView)
        {
            Assert.ThrowsExactly<NotSupportedException>(() => mutableView[1] = "replacement.json");
        }
    }
}

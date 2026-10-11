using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RuntimeBootstrapTests
{
    /// <summary>
    /// 保証：起動時の登録後にShift_JISを解決でき、UTF-8のコードページも維持して旧譜面の復号と既存UTF-8処理を両立する。
    /// </summary>
    [TestMethod]
    public void Initialize_RegistersLegacyCodePagesWithoutChangingUtf8()
    {
        RuntimeBootstrap.Initialize();

        Assert.AreEqual(932, Encoding.GetEncoding("shift_jis").CodePage);
        Assert.AreEqual(65001, Encoding.UTF8.CodePage);
    }
}

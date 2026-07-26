using System.Text;
using BeMusicSeeker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RuntimeBootstrapTests
{
    [TestMethod]
    public void Initialize_RegistersLegacyCodePagesWithoutChangingUtf8()
    {
        RuntimeBootstrap.Initialize();

        Assert.AreEqual(932, Encoding.GetEncoding("shift_jis").CodePage);
        Assert.AreEqual(65001, Encoding.UTF8.CodePage);
    }
}

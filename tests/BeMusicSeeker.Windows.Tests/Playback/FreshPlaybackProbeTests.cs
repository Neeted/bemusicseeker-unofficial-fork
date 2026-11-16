namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class FreshPlaybackProbeTests
{
    /// <summary>欠落・不一致・非zero・重複・stderr診断を、子の成功として受理する誤りを検出します。実子と全資源の終端は元三caseとOwnedProcessが分担します。</summary>
    [TestMethod]
    [DataRow(0, "", "")]
    [DataRow(0, "{\"Case\":\"other\",\"Passed\":true}", "")]
    [DataRow(2, "{\"Case\":\"requested\",\"Passed\":true}", "")]
    [DataRow(0, "{\"Case\":\"requested\",\"Passed\":true}\n{\"Case\":\"requested\",\"Passed\":true}", "")]
    [DataRow(0, "{\"Case\":\"requested\",\"Passed\":true}", "failure")]
    public void InvalidCompletionDoesNotSatisfyFreshProbeSuccess(int exitCode, string output, string error)
        => Assert.ThrowsExactly<AssertFailedException>(() => WindowsProcessProbeRunner.AssertSuccessfulResult("requested", new(exitCode, output, error)));
}

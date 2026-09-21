using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TemporaryCopyFilesTests
{
    [TestMethod]
    public void Dispose_RemovesEveryFileTrackedDuringParallelCopy()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_TemporaryCopy_" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "Source");
        string destinationDirectory = Path.Combine(root, "Destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        string[] sourceFiles = Enumerable.Range(0, 256)
            .Select(index => Path.Combine(sourceDirectory, index.ToString("D4") + ".dat"))
            .ToArray();
        try
        {
            foreach (string sourceFile in sourceFiles)
            {
                File.WriteAllText(sourceFile, "test");
            }

            using (new temporarilyCopyFiles(sourceFiles, destinationDirectory))
            {
                Assert.IsTrue(sourceFiles.All(source => File.Exists(Path.Combine(destinationDirectory, Path.GetFileName(source)))));
            }

            Assert.IsTrue(sourceFiles.All(source => !File.Exists(Path.Combine(destinationDirectory, Path.GetFileName(source)))));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

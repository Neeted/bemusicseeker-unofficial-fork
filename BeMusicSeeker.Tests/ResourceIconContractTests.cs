using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ResourceIconContractTests
{
    private static readonly IReadOnlyDictionary<string, Func<Icon>> TypedAccessors =
        new Dictionary<string, Func<Icon>>(StringComparer.Ordinal)
        {
            ["imageres_8"] = () => Images.imageres_8,
            ["imageres_18"] = () => Images.imageres_18,
            ["imageres_108"] = () => Images.imageres_108,
            ["imageres_131"] = () => Images.imageres_131,
            ["imageres_137"] = () => Images.imageres_137,
            ["imageres_180"] = () => Images.imageres_180,
            ["imageres_1004"] = () => Images.imageres_1004,
            ["imageres_5310"] = () => Images.imageres_5310,
            ["imageres_5311"] = () => Images.imageres_5311,
            ["imageres_5332"] = () => Images.imageres_5332,
            ["imageres_5342"] = () => Images.imageres_5342
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedPngDigests =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["imageres_8"] = "5B873F8A3DEBBD40029CD49875D06284A8245B2294628736BB24B0832220D2C5",
            ["imageres_18"] = "DF2EC9CF60EE0A4067C7C64069D014EBD59BD8805EE114D2C11CE9ADA47F5A6D",
            ["imageres_108"] = "8766A920CC0AA8E7288642B61F249B9D053E591644C39085DD82BD6B1BAFAFFF",
            ["imageres_131"] = "A48A30A0A86BDEEFBF577A261881317603D564066363F2429C164A173EFE3D32",
            ["imageres_137"] = "0748877851C5BBBF4937FB9765802532C1FF053F9F3CFB3CA6E89D963AF069A1",
            ["imageres_180"] = "A4EBD2047E1066BFB8283991CDAD5EC8EC81E4404D970568793EAFA9BDD6D31B",
            ["imageres_1004"] = "5CDEF275EA8C7AE0686F42C5EA566DCDB76570B42F2E28427D92079DB9C009CC",
            ["imageres_5310"] = "00842234012EF65B29340CF3A4BBEF690CE8C81E8CE451AE7FA342461D5CBDDB",
            ["imageres_5311"] = "798EC0CC2929DF22B8440E19E19DA5F833CA91441778641AB5B31DBA7BBF756B",
            ["imageres_5332"] = "FEE98BD505DE003A7456EA6184F14D1D02FD846D7DE6E5DB622DBA5A0455BAD5",
            ["imageres_5342"] = "B50533DF26BCD07772A45C78BCB1AD155F257EFAF373ACBCA4ED74E0C8E8A07D"
        };

    [TestMethod]
    public void EmbeddedImagesExposeStableKeysTypesAndPayloads()
    {
        TestUiDispatcherHost.Dispatcher.Invoke(() =>
        {
            ResourceSet resources = Images.ResourceManager.GetResourceSet(
                CultureInfo.InvariantCulture,
                createIfNotExists: true,
                tryParents: true) ?? throw new AssertFailedException("Images resource set is unavailable.");

            string[] resourceNames = resources
                .Cast<DictionaryEntry>()
                .Select(entry => entry.Key as string)
                .Where(name => name is not null && name.StartsWith("imageres_", StringComparison.Ordinal))
                .Cast<string>()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            string[] expectedNames = TypedAccessors.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(expectedNames, resourceNames);

            var mismatches = new List<string>();
            foreach (string name in expectedNames)
            {
                Icon managerIcon = (Icon)(Images.ResourceManager.GetObject(name, CultureInfo.InvariantCulture)
                    ?? throw new AssertFailedException($"ResourceManager returned no icon for {name}."));
                Icon typedIcon = TypedAccessors[name]();
                Assert.AreEqual(typeof(Icon), managerIcon.GetType(), $"ResourceManager type changed for {name}.");
                Assert.AreEqual(typeof(Icon), typedIcon.GetType(), $"Typed accessor type changed for {name}.");
                Assert.AreEqual(managerIcon.Width, typedIcon.Width, $"Width changed between resource paths for {name}.");
                Assert.AreEqual(managerIcon.Height, typedIcon.Height, $"Height changed between resource paths for {name}.");

                string digest = ComputePngDigest(managerIcon);
                if (!string.Equals(ExpectedPngDigests[name], digest, StringComparison.Ordinal))
                {
                    mismatches.Add($"{name}={digest}");
                }
            }

            if (mismatches.Count > 0)
            {
                Assert.Fail("PNG payload digests observed: " + string.Join(", ", mismatches));
            }
        });
    }

    [TestMethod]
    public void XamlIconConverterPreservesResourceDimensions()
    {
        TestUiDispatcherHost.Dispatcher.Invoke(() =>
        {
            var converter = new IconToImageSourceConverter();
            foreach (Func<Icon> accessor in TypedAccessors.Values)
            {
                Icon icon = accessor();
                var image = converter.Convert(icon, typeof(BitmapImage), parameter: null!, CultureInfo.InvariantCulture) as BitmapImage
                    ?? throw new AssertFailedException("Icon converter did not return a BitmapImage.");
                Assert.IsTrue(image.PixelWidth > 0);
                Assert.AreEqual(icon.Width, image.PixelWidth);
                Assert.AreEqual(icon.Height, image.PixelHeight);
            }
        });
    }

    private static string ComputePngDigest(Icon icon)
    {
        using Bitmap bitmap = icon.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}

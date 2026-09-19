using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows;
using System.Xml.Linq;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class NativeWindowThemeContractTests
{
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    [TestMethod]
    public void ConcreteProductionWindows_UseSharedThemedWindowExceptCustomChromeMainWindow()
    {
        Type[] exceptions = typeof(ThemedWindow).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(Window).IsAssignableFrom(type))
            .Where(type => !typeof(ThemedWindow).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { typeof(MainWindow) },
            exceptions,
            "MainWindow must remain the sole concrete production Window outside the shared ThemedWindow contract.");
    }

    [TestMethod]
    public void AuthoredWindowXaml_UsesSharedRootExceptCustomChromeMainWindow()
    {
        string repositoryRoot = FindRepositoryRoot();
        Assembly productionAssembly = typeof(ThemedWindow).Assembly;
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string path in EnumerateProductionXaml(repositoryRoot))
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            XElement root = document.Root!;
            string? className = (string?)root.Attribute(xamlNamespace + "Class");
            if (className == null)
            {
                continue;
            }

            Type? viewType = productionAssembly.GetType(className, throwOnError: false);
            if (viewType == null || !typeof(Window).IsAssignableFrom(viewType))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(repositoryRoot, path);
            if (viewType == typeof(MainWindow))
            {
                Assert.AreEqual("Window", root.Name.LocalName, relativePath);
                Assert.IsTrue(
                    root.Descendants().Any(element => element.Name.LocalName == "WindowChrome.WindowChrome"),
                    $"{relativePath} must retain its custom WindowChrome contract.");
                continue;
            }

            Assert.AreEqual("ThemedWindow", root.Name.LocalName, $"{relativePath} must use ThemedWindow as its XAML root.");
            StringAssert.StartsWith(
                root.Name.NamespaceName,
                "clr-namespace:BeMusicSeeker.Views",
                $"{relativePath} must resolve its root to BeMusicSeeker.Views.ThemedWindow.");

            if (string.Equals(className, "Parago.Windows.ProgressDialog", StringComparison.Ordinal))
            {
                Assert.AreEqual("True", root.Attributes().Single(attribute => attribute.Name.LocalName == "WindowSettings.HideCloseButton").Value);
                Assert.AreEqual("OnClosing", (string?)root.Attribute("Closing"));
            }
        }
    }

    [TestMethod]
    public void ProductionIl_DoesNotConstructRawSystemWindow()
    {
        Assembly productionAssembly = typeof(ThemedWindow).Assembly;
        var offenders = productionAssembly
            .GetTypes()
            .SelectMany(GetDeclaredMethods)
            .Where(ConstructsRawWindow)
            .Select(method => $"{method.DeclaringType?.FullName}.{method.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            offenders,
            "Production code must construct ThemedWindow or a derived type instead of System.Windows.Window directly.");
    }

    [TestMethod]
    public void ThemeDictionaries_DefineNativeWindowSemanticBrushes()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] requiredKeys =
        [
            "App.DialogBackgroundBrush",
            "App.TextBrush",
            "App.BorderBrush",
        ];

        foreach (string themeName in new[] { "Light.xaml", "Dark.xaml" })
        {
            string path = Path.Combine(repositoryRoot, "Themes", themeName);
            var document = XDocument.Load(path);
            XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
            var keys = document
                .Descendants()
                .Select(element => (string?)element.Attribute(xamlNamespace + "Key"))
                .Where(key => key != null)
                .Select(key => key!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string requiredKey in requiredKeys)
            {
                Assert.IsTrue(keys.Contains(requiredKey), $"{themeName} is missing {requiredKey}.");
            }
        }
    }

    private static IEnumerable<MethodBase> GetDeclaredMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
    }

    private static bool ConstructsRawWindow(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            return false;
        }

        int offset = 0;
        while (offset < il.Length)
        {
            OpCode opCode = ReadOpCode(il, ref offset);
            if (opCode == OpCodes.Newobj)
            {
                int metadataToken = BitConverter.ToInt32(il, offset);
                MethodBase? constructor = method.Module.ResolveMethod(
                    metadataToken,
                    method.DeclaringType?.GetGenericArguments(),
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (constructor is ConstructorInfo && constructor.DeclaringType == typeof(Window))
                {
                    return true;
                }
            }

            offset += GetOperandSize(opCode.OperandType, il, offset);
        }

        return false;
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        byte first = il[offset++];
        short value = first == 0xfe
            ? unchecked((short)(0xfe00 | il[offset++]))
            : first;
        return OpCodesByValue[value];
    }

    private static int GetOperandSize(OperandType operandType, byte[] il, int operandOffset)
    {
        return operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, operandOffset) * 4),
            _ => throw new InvalidOperationException($"Unsupported IL operand type: {operandType}."),
        };
    }

    private static IEnumerable<string> EnumerateProductionXaml(string repositoryRoot)
    {
        return new[] { "BeMusicSeeker", "Parago" }
            .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(repositoryRoot, directory), "*.xaml", SearchOption.AllDirectories))
            .Where(path => !HasDirectorySegment(path, "bin") && !HasDirectorySegment(path, "obj"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasDirectorySegment(string path, string segment)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

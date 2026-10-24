using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class NativeWindowThemeContractTests
{
    private const string ComponentThemeDictionaryPrefix = "/BeMusicSeeker;component/Themes/";

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    /// <summary>本番の具体Window型を全件確認し、独自chromeのMainWindow以外が共通テーマ更新・解除の境界を迂回しません。</summary>
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

    /// <summary>本番のXAML rootを実型と照合し、共通Window継承の抜けとprogressのClose抑制・Closing接続の欠落を検出します。</summary>
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

    /// <summary>本番のcompiled生成経路も走査し、型一覧だけでは見逃す直接Window生成によるテーマ境界の迂回を検出します。</summary>
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

    /// <summary>すべての配色辞書に題名部と共通部品が使う同じ意味付きbrushがあり、テーマ変更時の資源欠落を防ぎます。</summary>
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

        HashSet<string>? referenceKeys = null;
        foreach (string theme in AppThemeService.AvailableThemes)
        {
            string themeName = theme + ".xaml";
            string path = Path.Combine(repositoryRoot, "BeMusicSeeker", "Themes", themeName);
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

            if (referenceKeys == null)
            {
                referenceKeys = keys;
            }
            else
            {
                Assert.IsTrue(referenceKeys.SetEquals(keys), $"{themeName} must define the same semantic keys as Light.xaml.");
            }
        }
    }

    /// <summary>保存値は登録された識別子へ正規化し、題名部の明暗は配色系列ごとに判定します。</summary>
    [DataTestMethod]
    [DataRow("Light", "Light", false)]
    [DataRow("Dark", "Dark", true)]
    [DataRow("Sepia", "Sepia", false)]
    [DataRow("Frost", "Frost", false)]
    [DataRow("NeonViolet", "NeonViolet", true)]
    [DataRow("GreenTerminal", "GreenTerminal", true)]
    [DataRow("AmberTerminal", "AmberTerminal", true)]
    [DataRow("OledBlack", "OledBlack", true)]
    [DataRow("DeepSea", "DeepSea", true)]
    [DataRow("DosBlue", "DosBlue", true)]
    [DataRow("greenterminal", "GreenTerminal", true)]
    [DataRow("unknown-theme", "Light", false)]
    public void ThemeSettings_NormalizeSavedIdentifiersAndClassifyNativeCaption(string value, string expectedTheme, bool expectedDark)
    {
        Assert.AreEqual(expectedTheme, AppThemeService.NormalizeTheme(value));
        Assert.AreEqual(expectedDark, AppThemeService.IsDarkTheme(value));
    }

    /// <summary>各テーマから明暗の反対側へ往復切替し、共通style・変更通知・版更新とResourceAssemblyを保持します。</summary>
    [DataTestMethod]
    [DoNotParallelize]
    [DataRow("Light", "/BeMusicSeeker;component/Themes/Light.xaml")]
    [DataRow("Dark", "/BeMusicSeeker;component/Themes/Dark.xaml")]
    [DataRow("Sepia", "/BeMusicSeeker;component/Themes/Sepia.xaml")]
    [DataRow("Frost", "/BeMusicSeeker;component/Themes/Frost.xaml")]
    [DataRow("NeonViolet", "/BeMusicSeeker;component/Themes/NeonViolet.xaml")]
    [DataRow("GreenTerminal", "/BeMusicSeeker;component/Themes/GreenTerminal.xaml")]
    [DataRow("AmberTerminal", "/BeMusicSeeker;component/Themes/AmberTerminal.xaml")]
    [DataRow("OledBlack", "/BeMusicSeeker;component/Themes/OledBlack.xaml")]
    [DataRow("DeepSea", "/BeMusicSeeker;component/Themes/DeepSea.xaml")]
    [DataRow("DosBlue", "/BeMusicSeeker;component/Themes/DosBlue.xaml")]
    public void ApplyTheme_PreservesSharedDictionariesForSupportedThemeUris(
        string initialTheme,
        string initialThemeSource)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current
                ?? throw new InvalidOperationException("The shared WPF application host is unavailable.");
            Collection<ResourceDictionary> mergedDictionaries = application.Resources.MergedDictionaries;
            ResourceDictionary[] originalDictionaries = mergedDictionaries.ToArray();
            Assembly originalResourceAssembly = Application.ResourceAssembly;
            ResourceDictionary simpleStyles = CreateResourceDictionary(
                ComponentThemeDictionaryPrefix + "Simple Styles.xaml");
            ResourceDictionary canonicalDialogStyles = CreateResourceDictionary(
                ComponentThemeDictionaryPrefix + "CanonicalDialogStyles.xaml");
            ResourceDictionary palette = CreateResourceDictionary(initialThemeSource);
            int themeChangedCount = 0;
            EventHandler themeChangedHandler = (_, _) => themeChangedCount++;

            try
            {
                mergedDictionaries.Clear();
                mergedDictionaries.Add(simpleStyles);
                mergedDictionaries.Add(canonicalDialogStyles);
                mergedDictionaries.Add(palette);
                CustomTablePalette.Invalidate();

                AppThemeService.ThemeChanged += themeChangedHandler;
                try
                {
                    AppThemeService.ApplyTheme(initialTheme);
                    AssertAppliedThemeResources(application, initialTheme, simpleStyles, canonicalDialogStyles);
                    int versionAfterInitialApply = AppThemeService.Version;
                    int themeChangedCountAfterInitialApply = themeChangedCount;

                    string oppositeTheme = AppThemeService.IsDarkTheme(initialTheme)
                        ? AppThemeService.Light
                        : AppThemeService.Dark;
                    AppThemeService.ApplyTheme(oppositeTheme);
                    Assert.IsTrue(AppThemeService.Version > versionAfterInitialApply);
                    Assert.AreEqual(themeChangedCountAfterInitialApply + 1, themeChangedCount);
                    AssertAppliedThemeResources(application, oppositeTheme, simpleStyles, canonicalDialogStyles);
                    int versionAfterOppositeApply = AppThemeService.Version;
                    int themeChangedCountAfterOppositeApply = themeChangedCount;

                    AppThemeService.ApplyTheme(initialTheme);
                    Assert.IsTrue(AppThemeService.Version > versionAfterOppositeApply);
                    Assert.AreEqual(themeChangedCountAfterOppositeApply + 1, themeChangedCount);
                    AssertAppliedThemeResources(application, initialTheme, simpleStyles, canonicalDialogStyles);
                    Assert.AreSame(originalResourceAssembly, Application.ResourceAssembly);
                }
                finally
                {
                    AppThemeService.ThemeChanged -= themeChangedHandler;
                }
            }
            finally
            {
                mergedDictionaries.Clear();
                foreach (ResourceDictionary dictionary in originalDictionaries)
                {
                    mergedDictionaries.Add(dictionary);
                }

                CustomTablePalette.Invalidate();
            }
        });
    }

    /// <summary>明示Styleのない実ToolTipを表示したまま明暗を往復し、背景・枠・本文と文字列/折返し本文の継承が内容を失わず切り替わります。</summary>
    /// <remarks>IsOpenで標準ToolTipの実Popupを描画します。文字列ToolTipの自動生成・明示生成・AutoToolTipから型styleへの接続は静的確認へ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void ToolTip_VisibleContentTracksThemeWithoutExplicitStyle()
    {
        List<Exception> cleanupFailures = [];
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                void Cleanup(Action cleanup)
                {
                    try { cleanup(); }
                    catch (Exception failure) { cleanupFailures.Add(failure); }
                }
                Application application = Application.Current;
                ResourceDictionary[] originalDictionaries = application.Resources.MergedDictionaries.ToArray();
                var owner = new Window { Content = new Border(), Width = 240, Height = 120 };
                var toolTip = new ToolTip { PlacementTarget = owner, Placement = PlacementMode.Bottom, StaysOpen = true };
                scope.TrackToolTip(toolTip);
                try
                {
                    scope.ShowAndWaitForContentRendered(owner);
                    AppThemeService.ApplyTheme(AppThemeService.Light);
                    var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    toolTip.Opened += (_, _) => opened.TrySetResult();
                    toolTip.Content = "テーマ切替中も保持する本文";
                    toolTip.IsOpen = true;
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(opened.Task, "ToolTip.Opened");
                    nint handle = TestWindowPresentationScope.GetNativeHandle(toolTip);
                    Assert.IsTrue(TestWindowPresentationScope.HasNoActivateStyle(handle));
                    Assert.IsTrue(TestWindowPresentationScope.IsOutsideAllMonitors(handle));
                    var wrapped = new TextBlock { Text = "折返し本文をテーマに追随させる", TextWrapping = TextWrapping.Wrap, Width = 100 };
                    foreach (object content in new object[] { toolTip.Content, wrapped })
                    {
                        toolTip.Content = content;
                        foreach (string theme in new[] { AppThemeService.Light, AppThemeService.Dark, AppThemeService.Light })
                        {
                            AppThemeService.ApplyTheme(theme);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            toolTip.UpdateLayout();
                            Assert.IsTrue(toolTip.IsOpen && toolTip.IsVisible);
                            Assert.AreSame(content, toolTip.Content);
                            Color background = ((SolidColorBrush)application.FindResource("App.PopupBackgroundBrush")).Color;
                            Color foreground = ((SolidColorBrush)application.FindResource("App.TextBrush")).Color;
                            Color border = ((SolidColorBrush)application.FindResource("App.BorderBrush")).Color;
                            Assert.AreEqual(background, ((SolidColorBrush)toolTip.Background).Color);
                            Assert.AreEqual(foreground, ((SolidColorBrush)toolTip.Foreground).Color);
                            Assert.AreEqual(border, ((SolidColorBrush)toolTip.BorderBrush).Color);
                            Border surface = VisualDescendants(toolTip).OfType<Border>().First(item => item.BorderThickness.Top > 0);
                            Assert.AreEqual(background, ((SolidColorBrush)surface.Background).Color);
                            Assert.AreEqual(border, ((SolidColorBrush)surface.BorderBrush).Color);
                            TextBlock body = VisualDescendants(toolTip).OfType<TextBlock>().Single();
                            Assert.AreEqual(foreground, ((SolidColorBrush)body.Foreground).Color);
                            Assert.AreEqual(content is string text ? text : wrapped.Text, body.Text);
                            if (ReferenceEquals(content, wrapped)) { Assert.AreEqual(TextWrapping.Wrap, body.TextWrapping); }
                        }
                    }
                }
                finally
                {
                    // 閉鎖終端を先に試み、各cleanupの故障で後続の資源回収・共有辞書の復元を飛ばしません。
                    Cleanup(() =>
                    {
                        if (!toolTip.IsOpen) { return; }
                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        RoutedEventHandler onClosed = (_, _) => closed.TrySetResult();
                        toolTip.Closed += onClosed;
                        try
                        {
                            toolTip.IsOpen = false;
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "ToolTip.Closed");
                        }
                        finally { toolTip.Closed -= onClosed; }
                    });
                    Cleanup(() =>
                    {
                        if (!application.Windows.Cast<Window>().Contains(owner)) { return; }
                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        EventHandler onClosed = (_, _) => closed.TrySetResult();
                        owner.Closed += onClosed;
                        try
                        {
                            owner.Close();
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "ToolTip.Owner.Closed");
                        }
                        finally { owner.Closed -= onClosed; }
                    });
                    Cleanup(application.Resources.MergedDictionaries.Clear);
                    foreach (ResourceDictionary dictionary in originalDictionaries)
                    { Cleanup(() => application.Resources.MergedDictionaries.Add(dictionary)); }
                    Cleanup(CustomTablePalette.Invalidate);
                }
            });
        }
        catch (Exception failure)
        {
            // scopeが選ぶ本文・表示観測の失敗を優先し、局所cleanupの二次故障を添付します。
            if (cleanupFailures.Count != 0)
            {
                failure.Data["ToolTipCleanupFailures"] = new AggregateException(cleanupFailures).ToString();
            }
            throw;
        }
        if (cleanupFailures.Count != 0) { throw new AggregateException("ToolTip fixture cleanup failed.", cleanupFailures); }
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in VisualDescendants(child)) { yield return descendant; }
        }
    }

    private static void AssertAppliedThemeResources(
        Application application,
        string expectedTheme,
        ResourceDictionary simpleStyles,
        ResourceDictionary canonicalDialogStyles)
    {
        Collection<ResourceDictionary> mergedDictionaries = application.Resources.MergedDictionaries;
        Assert.IsTrue(mergedDictionaries.Contains(simpleStyles), "Simple Styles must survive theme replacement.");
        Assert.IsTrue(
            mergedDictionaries.Contains(canonicalDialogStyles),
            "Canonical dialog styles must survive theme replacement.");

        ResourceDictionary[] themeDictionaries = mergedDictionaries
            .Where(dictionary => !ReferenceEquals(dictionary, simpleStyles)
                && !ReferenceEquals(dictionary, canonicalDialogStyles))
            .ToArray();
        Assert.AreEqual(1, themeDictionaries.Length, "Exactly one theme palette must be applied.");
        Assert.AreEqual(
            ComponentThemeDictionaryPrefix + expectedTheme + ".xaml",
            themeDictionaries[0].Source?.OriginalString);

        Assert.IsInstanceOfType(
            application.TryFindResource("SimpleButton"),
            typeof(Style),
            "SimpleButton must remain resolvable from the shared Simple Styles dictionary.");
        Assert.IsInstanceOfType(
            application.TryFindResource("App.Canonical.DialogActionStyle"),
            typeof(Style),
            "DialogActionStyle must remain resolvable from the shared canonical dictionary.");
    }

    private static ResourceDictionary CreateResourceDictionary(string source)
    {
        ResourceDictionary dictionary = new();
        dictionary.Source = new Uri(source, UriKind.RelativeOrAbsolute);
        return dictionary;
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
        return new[] { "BeMusicSeeker" }
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

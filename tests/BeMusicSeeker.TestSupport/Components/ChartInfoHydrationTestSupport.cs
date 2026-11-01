using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>旧chart-infoと移管先playlistが必要とする実hydrationの起動・終端観測だけを共有します。</summary>
internal static class ChartInfoHydrationTestSupport
{

    /// <summary>実hydrationの非公開起動口へ既存のreasonと後続backfill指定を渡し、反射呼出しの失敗もそのまま伝播します。</summary>
    internal static void InvokeDeferredChartInfoHydration(BMSLibrary library, string reason, bool queueFullBackfillAfterHydration)
    {
        MethodInfo? method = typeof(BMSLibrary).GetMethod("QueueDeferredChartInfoHydration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "QueueDeferredChartInfoHydration method was not found.");
        method!.Invoke(library, [reason, queueFullBackfillAfterHydration]);
    }

    /// <summary>要求versionの完了と非実行状態が揃う通知を待ち、観測Taskの失敗を呼出元へ返します。</summary>
    internal static Task AwaitChartInfoHydrationAsync(BMSLibrary library)
    {
        return AwaitChartInfoStateAsync(
            library,
            current => current.ChartInfoHydrationRequestedVersion > 0
                && current.ChartInfoHydrationCompletedVersion == current.ChartInfoHydrationRequestedVersion
                && !current.ChartInfoHydrationRunning,
            nameof(BMSLibrary.ChartInfoHydrationRequestedVersion),
            nameof(BMSLibrary.ChartInfoHydrationCompletedVersion),
            nameof(BMSLibrary.ChartInfoHydrationRunning));
    }

    /// <summary>指定した実モデルの状態通知を購読し、完了または観測失敗で解除して同じTaskへ結果を伝えます。</summary>
    internal static Task AwaitChartInfoStateAsync(
        BMSLibrary library,
        Func<BMSLibrary, bool> isComplete,
        params string[] observedPropertyNames)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void CompleteIfReady()
        {
            try
            {
                if (isComplete(library))
                {
                    library.PropertyChanged -= OnPropertyChanged;
                    completion.TrySetResult(null);
                }
            }
            catch (Exception exception)
            {
                library.PropertyChanged -= OnPropertyChanged;
                completion.TrySetException(exception);
            }
        }

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is null || Array.IndexOf(observedPropertyNames, args.PropertyName) >= 0)
            {
                CompleteIfReady();
            }
        }

        library.PropertyChanged += OnPropertyChanged;
        CompleteIfReady();
        // 状態を受信したTask自体を返し、後続continuationの実行遅延を通知欠落と誤認しない。
        return completion.Task;
    }


    /// <summary>画面を開かず、指定されたdefault応答または従来のOK応答をそのまま返すdialog入力です。</summary>
    internal sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }


    internal static void InvokeInstallChartPackages(BMSLibrary library, IEnumerable<ChartPackage> packages, string installDirectory)
    {
        List<ChartPackage> packageList = [.. (packages ?? [])];
        foreach (PackageChartEntry entry in packageList
            .SelectMany(package => package?.ChartEntries ?? [])
            .Where(entry => entry?.Chart != null))
        {
            entry.SetInstallDestinationPathOnly(installDirectory);
        }

        library.ChartPackagesPending = new ObservableCollection<ChartPackage>(packageList);
        library.InstallPendingPackagesToEstimatedDestinations(packageList);
    }

    internal static Task AwaitChartInfoBackfillAsync(BMSLibrary library)
    {
        return AwaitChartInfoStateAsync(
            library,
            current => current.ChartInfoBackfillRequestedVersion > 0
                && current.ChartInfoBackfillCompletedVersion == current.ChartInfoBackfillRequestedVersion
                && !current.ChartInfoBackfillRunning,
            nameof(BMSLibrary.ChartInfoBackfillRequestedVersion),
            nameof(BMSLibrary.ChartInfoBackfillCompletedVersion),
            nameof(BMSLibrary.ChartInfoBackfillRunning));
    }
}

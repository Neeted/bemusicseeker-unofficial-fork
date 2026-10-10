using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>共通値と管理主体間の代表的な受渡しが、保存行を保持しない契約を構築済み型で検査します。</summary>
[TestClass]
public sealed class CommonChartPayloadContractTests
{
    [TestMethod]
    public void CompiledCommonValuesAndRequestsDoNotContainStorageRows()
    {
        Type[] boundaries =
        [
            typeof(ChartFile), typeof(ChartDetails), typeof(ChartParseFailure),
            typeof(ResourceHealthMaintenanceSnapshot), typeof(LibraryChartRef),
            typeof(CatalogChartInfoWriteRequest), typeof(CatalogChartInfoStorageWriteRequest),
            typeof(CatalogMaintenanceWriteRequest), typeof(CatalogRelocationRequest),
            typeof(CatalogInstalledTargetUpsertRequest), typeof(ChartStorageTargetSet),
            typeof(FileScanDiffCommitChunk), typeof(ChartInfoInlineBuildResult),
            typeof(ChartDigestBackfillResult), typeof(Lr2SongDbSyncInput),
            typeof(LibraryFileInitializationResult), typeof(Lr2SongDbSyncInputRowSnapshot),
            typeof(Lr2SongDbSyncScanSurfaceSnapshot), typeof(CatalogChartMutationFact), typeof(CatalogRelocationPathFact),
            typeof(NormalLibraryRefreshNotification), typeof(PackageChartEntry)
        ];
        var visited = new HashSet<Type>();
        foreach (Type boundary in boundaries)
        {
            AssertNoStoragePayload(boundary, boundary.Name, visited);
            foreach (MethodInfo method in boundary.GetMethods(BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(method => !method.IsPrivate))
            {
                AssertNoStoragePayload(method.ReturnType, boundary.Name + "." + method.Name + " return", visited);
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    AssertNoStoragePayload(parameter.ParameterType, boundary.Name + "." + method.Name + " " + parameter.Name, visited);
                }
            }
        }
        Assert.IsNull(typeof(OwnedChartToken).BaseType?.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault());
        Assert.AreEqual(0, typeof(OwnedChartToken).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length);
        Assert.AreEqual(0, typeof(OwnedChartToken).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length);
        Assert.IsTrue(typeof(OwnedChartToken).IsSealed);
        foreach (Type parser in new[] { typeof(BmsChartFileParser), typeof(BmsonChartFileParser) })
        {
            MethodInfo[] methods = parser.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(method => method.Name == "ParseSnapshot").ToArray();
            Assert.IsTrue(methods.Length > 0);
            Assert.IsTrue(methods.All(method => method.ReturnType == typeof(ChartFile)));
        }
    }

    private static void AssertNoStoragePayload(Type type, string location, HashSet<Type> visited)
    {
        if (type.HasElementType)
        {
            AssertNoStoragePayload(type.GetElementType() ?? throw new InvalidOperationException(), location, visited);
        }
        foreach (Type argument in type.GetGenericArguments())
        {
            AssertNoStoragePayload(argument, location, visited);
        }
        Assert.IsFalse(typeof(LR2SongDB.song).IsAssignableFrom(type)
            || typeof(LR2SongDBExtended.bmson_song).IsAssignableFrom(type)
            || typeof(LR2SongDBExtended.chart_info).IsAssignableFrom(type)
            || typeof(LR2SongDBExtended.chart_info_parse_failure).IsAssignableFrom(type)
            || typeof(LR2SongDBExtended.maintenance).IsAssignableFrom(type), location + " -> " + type.FullName);
        if (type.Assembly != typeof(ChartFile).Assembly || !visited.Add(type))
        {
            return;
        }
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            AssertNoStoragePayload(field.FieldType, location + "." + field.Name, visited);
        }
    }
}

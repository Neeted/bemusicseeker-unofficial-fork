using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class SevenZipArchiveExtractor
{
    public static string ResolveBundledSevenZipLibraryPath()
    {
        string architectureDirectoryName = IntPtr.Size == 4 ? "x86" : "x64";
        string bundledLibraryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs", architectureDirectoryName, "7z.dll");
        if (File.Exists(bundledLibraryPath))
        {
            return bundledLibraryPath;
        }

        throw new FileNotFoundException(Resources.Warn_ArchiveBundledSevenZipMissingDetail, bundledLibraryPath);
    }

    public static string ResolveBundledSevenZipExtractorAssemblyPath()
    {
        string managedAssemblyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs", "SevenZipExtractor.dll");
        if (File.Exists(managedAssemblyPath))
        {
            return managedAssemblyPath;
        }

        throw new FileNotFoundException(Resources.Warn_ArchiveBundledSevenZipMissingDetail, managedAssemblyPath);
    }

    public static List<ArchiveEntryMetadata> ExtractArchiveEntries(string archivePath, string extractedTempDirectoryPath)
    {
        return ExtractArchiveEntries(archivePath, ResolveBundledSevenZipLibraryPath(), extractedTempDirectoryPath);
    }

    public static List<ArchiveEntryMetadata> ExtractArchiveEntries(string archivePath, string nativeLibraryPath, string extractedTempDirectoryPath)
    {
        string managedAssemblyPath = ResolveBundledSevenZipExtractorAssemblyPath();
        var managedAssembly = Assembly.LoadFrom(managedAssemblyPath);
        Type archiveFileType = GetRequiredType(managedAssembly, "SevenZipExtractor.ArchiveFile");
        Type entryType = GetRequiredType(managedAssembly, "SevenZipExtractor.Entry");
        ConstructorInfo archiveFileConstructor = GetRequiredConstructor(archiveFileType, typeof(string), typeof(string));
        MethodInfo extractMethod = GetRequiredMethod(archiveFileType, "Extract", typeof(string), typeof(bool), typeof(string));
        PropertyInfo entriesProperty = GetRequiredProperty(archiveFileType, "Entries");
        PropertyInfo fileNameProperty = GetRequiredProperty(entryType, "FileName");
        PropertyInfo isFolderProperty = GetRequiredProperty(entryType, "IsFolder");
        PropertyInfo creationTimeProperty = GetRequiredProperty(entryType, "CreationTime");
        PropertyInfo lastAccessTimeProperty = GetRequiredProperty(entryType, "LastAccessTime");
        PropertyInfo lastWriteTimeProperty = GetRequiredProperty(entryType, "LastWriteTime");
        List<ArchiveEntryMetadata> archiveEntries = [];
        // Load the bundled managed wrapper/native library pair explicitly so archive handling
        // stays deterministic even when machine-level 7z registrations are present.
        using (var archiveFile = (IDisposable)InvokeConstructor(archiveFileConstructor, archivePath, nativeLibraryPath))
        {
            InvokeMethod(extractMethod, archiveFile, extractedTempDirectoryPath, false, null);
            IEnumerable reflectedEntries = GetPropertyValue<IEnumerable>(entriesProperty, archiveFile);
            if (reflectedEntries == null)
            {
                return archiveEntries;
            }

            foreach (object reflectedEntry in reflectedEntries)
            {
                archiveEntries.Add(new ArchiveEntryMetadata
                {
                    FileName = GetPropertyValue<string>(fileNameProperty, reflectedEntry),
                    IsFolder = GetPropertyValue<bool>(isFolderProperty, reflectedEntry),
                    CreationTime = GetPropertyValue<DateTime>(creationTimeProperty, reflectedEntry),
                    LastAccessTime = GetPropertyValue<DateTime>(lastAccessTimeProperty, reflectedEntry),
                    LastWriteTime = GetPropertyValue<DateTime>(lastWriteTimeProperty, reflectedEntry)
                });
            }
        }

        return archiveEntries;
    }

    private static Type GetRequiredType(Assembly assembly, string typeName)
    {
        Type reflectedType = assembly?.GetType(typeName, throwOnError: false);
        if (reflectedType != null)
        {
            return reflectedType;
        }

        throw new MissingMemberException(assembly?.FullName ?? "(unknown assembly)", typeName);
    }

    private static ConstructorInfo GetRequiredConstructor(Type declaringType, params Type[] parameterTypes)
    {
        ConstructorInfo constructor = declaringType?.GetConstructor(parameterTypes);
        if (constructor != null)
        {
            return constructor;
        }

        throw new MissingMethodException(declaringType?.FullName ?? "(unknown type)", ".ctor");
    }

    private static MethodInfo GetRequiredMethod(Type declaringType, string methodName, params Type[] parameterTypes)
    {
        MethodInfo method = declaringType?.GetMethod(methodName, parameterTypes);
        if (method != null)
        {
            return method;
        }

        throw new MissingMethodException(declaringType?.FullName ?? "(unknown type)", methodName);
    }

    private static PropertyInfo GetRequiredProperty(Type declaringType, string propertyName)
    {
        PropertyInfo property = declaringType?.GetProperty(propertyName);
        if (property != null)
        {
            return property;
        }

        throw new MissingMemberException(declaringType?.FullName ?? "(unknown type)", propertyName);
    }

    private static object InvokeConstructor(ConstructorInfo constructor, params object[] arguments)
    {
        try
        {
            return constructor.Invoke(arguments);
        }
        catch (TargetInvocationException invocationException) when (invocationException.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(invocationException.InnerException).Throw();
            throw;
        }
    }

    private static void InvokeMethod(MethodInfo method, object target, params object[] arguments)
    {
        try
        {
            method.Invoke(target, arguments);
        }
        catch (TargetInvocationException invocationException) when (invocationException.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(invocationException.InnerException).Throw();
            throw;
        }
    }

    private static T GetPropertyValue<T>(PropertyInfo property, object target)
    {
        try
        {
            return (T)property.GetValue(target);
        }
        catch (TargetInvocationException invocationException) when (invocationException.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(invocationException.InnerException).Throw();
            throw;
        }
    }
}

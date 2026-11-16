#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BeMusicSeeker.Runtime;

/// <summary>通常起動全体の互換排他と、通常・検証起動共通のプロファイル排他を同じスレッドで所有します。</summary>
internal sealed class ApplicationInstanceLease : IDisposable
{
    /// <summary>通常起動が従来から使う、配置に依存しない排他名です。</summary>
    internal const string NormalMutexName = "a601b8c6-41c3-4182-950b-b96a0f0c8b0c";

    private readonly Mutex? normalMutex;
    private readonly Mutex profileMutex;
    private bool disposed;

    private ApplicationInstanceLease(Mutex? normalMutex, Mutex profileMutex)
    {
        this.normalMutex = normalMutex;
        this.profileMutex = profileMutex;
    }

    /// <summary>絶対パス・末尾区切り・Windowsの大小文字を正規化し、実行IDに依存しない排他名を返します。</summary>
    internal static string GetProfileMutexName(string profileDirectory)
    {
        string identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory)).ToUpperInvariant();
        return "Local\\BeMusicSeeker.Profile." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    /// <summary>通常起動では全体、続いてprofileを取得します。失敗時は途中取得も同じ呼出スレッドで解放します。</summary>
    /// <param name="normalMutexName">通常は固定名。実OS検証では利用者のアプリと競合しない専有名を渡します。</param>
    internal static ApplicationInstanceLease? TryAcquire(string profileDirectory, bool isTestSession,
        TimeSpan timeout, string normalMutexName = NormalMutexName)
    {
        Mutex? normal = null;
        Mutex? profile = null;
        bool normalOwned = false;
        bool profileOwned = false;
        try
        {
            if (!isTestSession)
            {
                normal = new Mutex(false, normalMutexName);
                normalOwned = Acquire(normal, timeout);
                if (!normalOwned) { return null; }
            }
            profile = new Mutex(false, GetProfileMutexName(profileDirectory));
            profileOwned = Acquire(profile, timeout);
            if (!profileOwned) { return null; }
            return new ApplicationInstanceLease(normal, profile);
        }
        finally
        {
            if (!profileOwned)
            {
                profile?.Dispose();
                if (normalOwned) { normal?.ReleaseMutex(); }
                normal?.Dispose();
            }
        }
    }

    private static bool Acquire(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>取得スレッドから一度だけ解放します。未取得のMutexはこの所有者へ渡しません。</summary>
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        try { profileMutex.ReleaseMutex(); }
        finally
        {
            profileMutex.Dispose();
            if (normalMutex != null)
            {
                try { normalMutex.ReleaseMutex(); }
                finally { normalMutex.Dispose(); }
            }
        }
    }
}

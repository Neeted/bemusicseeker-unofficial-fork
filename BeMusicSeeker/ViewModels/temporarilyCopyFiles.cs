using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

internal class temporarilyCopyFiles : IDisposable
{
    private readonly int sleep;

    private readonly List<string> copiedFilesSrc = [];

    private readonly List<string> copiedFilesDst = [];

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (sleep > 0)
        {
            Thread.Sleep(sleep);
        }
        copiedFilesDst.AsParallel().ForAll(delegate (string dstFile)
        {
            try
            {
                if (LongPathFileSystem.FileExists(dstFile))
                {
                    LongPathFileSystem.DeleteFile(dstFile);
                }
                else if (LongPathFileSystem.DirectoryExists(dstFile))
                {
                    LongPathFileSystem.DeleteDirectory(dstFile, recursive: true);
                }
            }
            catch (Exception ex)
            {
                string message = Resources.Msg_error_preview + Environment.NewLine + Environment.NewLine + Resources.File + ": " + dstFile.ToString() + Environment.NewLine + ex.Message;
                if (disposing)
                {
                    UiDialogRoute.ShowMessageBox(message, Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                }
                else
                {
                    NLogWrapper.FileLogger?.Warn(ex, "temporary_preview_cleanup_failed path=" + dstFile);
                }
            }
        });
    }

    public temporarilyCopyFiles(IEnumerable<string> srcFiles, string dstDir, int wait = 0)
    {
        temporarilyCopyFiles temporarilyCopyFiles2 = this;
        if (srcFiles == null)
        {
            throw new ArgumentNullException("srcFiles");
        }
        if (dstDir == null)
        {
            throw new ArgumentNullException("dstDir");
        }
        if (!srcFiles.All(LongPathFileSystem.EntryExists))
        {
            throw new ArgumentException("srcFiles");
        }
        if (!LongPathFileSystem.DirectoryExists(dstDir))
        {
            throw new DirectoryNotFoundException(dstDir);
        }
        if (wait > 0)
        {
            sleep = wait;
        }
        copiedFilesSrc = [.. srcFiles.Where(delegate (string file)
        {
            string path = Path.Combine(dstDir, Path.GetFileName(file));
            return !LongPathFileSystem.EntryExists(path);
        })];
        try
        {
            copiedFilesSrc.AsParallel().ForAll(delegate (string srcFile)
            {
                try
                {
                    string text = Path.Combine(dstDir, Path.GetFileName(srcFile));
                    if (LongPathFileSystem.FileExists(srcFile))
                    {
                        LongPathFileSystem.CopyFile(srcFile, text, overwrite: false);
                    }
                    else
                    {
                        if (!LongPathFileSystem.DirectoryExists(srcFile))
                        {
                            return;
                        }
                        LongPathFileSystem.CopyDirectory(srcFile, text, overwrite: false);
                    }
                    lock (temporarilyCopyFiles2.copiedFilesDst)
                    {
                        temporarilyCopyFiles2.copiedFilesDst.Add(text);
                    }
                }
                catch (Exception ex)
                {
                    UiDialogRoute.ShowMessageBox(Resources.Msg_warn_preview + Environment.NewLine + ex.Message, Resources.Error, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    throw;
                }
            });
        }
        catch
        {
            Dispose();
            copiedFilesDst = [];
            throw;
        }
    }

    ~temporarilyCopyFiles()
    {
        Dispose(disposing: false);
    }
}

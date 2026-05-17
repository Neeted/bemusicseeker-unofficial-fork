using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualBasic.FileIO;

namespace BeMusicSeeker.ViewModels;

internal class temporarilyCopyFiles : IDisposable
{
    private int sleep;

    private List<string> copiedFilesSrc = new List<string>();

    private List<string> copiedFilesDst = new List<string>();

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
                if (File.Exists(dstFile))
                {
                    FileSystem.DeleteFile(dstFile);
                }
                else if (Directory.Exists(dstFile))
                {
                    FileSystem.DeleteDirectory(dstFile, DeleteDirectoryOption.DeleteAllContents);
                }
            }
            catch (Exception ex)
            {
                DispatcherMessageBox.Show(Resources.Msg_error_preview + Environment.NewLine + Environment.NewLine + Resources.File + ": " + dstFile.ToString() + Environment.NewLine + ex.Message, Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
        if (!srcFiles.All((string file) => File.Exists(file) || Directory.Exists(file)))
        {
            throw new ArgumentException("srcFiles");
        }
        if (!Directory.Exists(dstDir))
        {
            throw new DirectoryNotFoundException(dstDir);
        }
        if (wait > 0)
        {
            sleep = wait;
        }
        copiedFilesSrc = srcFiles.Where(delegate (string file)
        {
            string path = Path.Combine(dstDir, Path.GetFileName(file));
            return !File.Exists(path) && !Directory.Exists(path);
        }).ToList();
        try
        {
            copiedFilesSrc.AsParallel().ForAll(delegate (string srcFile)
            {
                try
                {
                    string text = Path.Combine(dstDir, Path.GetFileName(srcFile));
                    if (File.Exists(srcFile))
                    {
                        FileSystem.CopyFile(srcFile, text);
                    }
                    else
                    {
                        if (!Directory.Exists(srcFile))
                        {
                            return;
                        }
                        FileSystem.CopyDirectory(srcFile, text);
                    }
                    temporarilyCopyFiles2.copiedFilesDst.Add(text);
                }
                catch (Exception ex)
                {
                    DispatcherMessageBox.Show(Resources.Msg_warn_preview + Environment.NewLine + ex.Message, Resources.Error, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                    throw;
                }
            });
        }
        catch
        {
            Dispose();
            copiedFilesDst = new List<string>();
        }
    }

    ~temporarilyCopyFiles()
    {
        Dispose(disposing: false);
    }
}

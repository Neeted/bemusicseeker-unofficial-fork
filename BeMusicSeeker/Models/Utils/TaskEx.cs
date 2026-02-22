using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using NLog;
using Ribbit.Logging;
using Ribbit.Util.Extensions;
using SQLite;

namespace BeMusicSeeker.Models.Utils;

public static class TaskEx
{
	private static readonly Logger loggerLocal = NLogWrapper.FileLogger;

	private static readonly Logger loggerPost = NLogWrapper.NetworkLogger;

	private static void log2file(Task x, string memberName, string filePath, int lineNumber)
	{
		if (!x.IsFaulted)
		{
			return;
		}
		if (x.Exception != null)
		{
			if (x.Exception.Flatten().InnerExceptions.Count == 1 && x.Exception.Flatten().InnerExceptions.Any((Exception e) => e is TaskCanceledException))
			{
				return;
			}
			if (x.Exception.Flatten().InnerExceptions.Any((Exception e) => e is PathTooLongException))
			{
				DispatcherMessageBox.Show("操作の途中で長すぎるパスが検出されました。" + Environment.NewLine + "プログラムを終了し取り扱うファイルの最大パス長が260文字未満であることを確認して下さい。", "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
				return;
			}
			if (x.Exception.Flatten().InnerExceptions.Any((Exception e) => e is IOException))
			{
				DispatcherMessageBox.Show("操作の途中でファイルのアクセスに失敗しました。" + Environment.NewLine + "エラー概要:" + Environment.NewLine + string.Join(Environment.NewLine, x.Exception.Flatten().InnerExceptions.Select((Exception e) => e.Message)) + Environment.NewLine + Environment.NewLine + "プログラムを終了しファイルへのアクセスが可能か確認して下さい", "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
				return;
			}
			if (x.Exception.Flatten().InnerExceptions.Any((Exception e) => e is OutOfMemoryException))
			{
				DispatcherMessageBox.Show("操作の途中でメモリ不足が検出されました。" + Environment.NewLine + "搭載メモリが4GB以上の場合、32BITアプリケーションの制限によるものです。" + Environment.NewLine + Environment.NewLine + "・他のプロセスがメモリを消費している" + Environment.NewLine + "・管理可能ファイル数を大幅に超えている" + Environment.NewLine + "・プログラムの不具合" + Environment.NewLine + "等が原因として考えられます。" + Environment.NewLine + "開発者へ情報を送信し詳しい発生状況について連絡いただけると改善出来る可能性があります。", "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
			}
			else if (x.Exception.Flatten().InnerExceptions.Any((Exception e) => e is SQLiteException && ((SQLiteException)e).Result == SQLite3.Result.Busy))
			{
				DispatcherMessageBox.Show("操作の途中でDBアクセスビジーが検出されました。" + Environment.NewLine + "DBにアクセスする他のプロセス、LR2、またはツールの二重起動" + Environment.NewLine + "等により同時アクセスが発生している可能性があります。" + Environment.NewLine, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
			}
			else if (x.Exception.Flatten().InnerExceptions.Any((Exception e) => e is SQLiteException && ((SQLiteException)e).Result == SQLite3.Result.Locked))
			{
				DispatcherMessageBox.Show("操作の途中でDBロックが検出されました。" + Environment.NewLine + "DBにアクセスする他のプロセス、LR2、またはツールの二重起動" + Environment.NewLine + "等により同時アクセスが発生している可能性があります。" + Environment.NewLine, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
			}
		}
		Logger logger = loggerLocal;
		string text = Assembly.GetEntryAssembly().GetName().Version.ToString();
		if (DispatcherMessageBox.Show("予期しないエラーが発生しました。" + Environment.NewLine + Environment.NewLine + "エラーの発生状況を開発者に送信してもよろしいでしょうか?" + Environment.NewLine + "送信される情報は発生箇所の特定に使用され、" + Environment.NewLine + "個人の情報は含まれません。" + Environment.NewLine + Environment.NewLine + "エラー概要:" + Environment.NewLine + string.Join(Environment.NewLine, x.Exception.Flatten().InnerExceptions.Select((Exception e) => e.Message)), "エラー", MessageBoxButton.YesNo, MessageBoxImage.Hand, MessageBoxResult.Yes) == MessageBoxResult.Yes)
		{
			logger = loggerPost;
		}
		try
		{
			logger?.Error(x.Exception, text + " - " + memberName + Environment.NewLine + x.Exception.ToString());
		}
		catch
		{
		}
	}

	public static Task Logging(this Task task, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
	{
		return task.Logging(log2file, memberName, filePath, lineNumber);
	}

	public static Task<T> Logging<T>(this Task<T> task, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
	{
		return task.Logging(log2file, memberName, filePath, lineNumber);
	}
}

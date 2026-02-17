using System;
using System.ComponentModel;
using System.Deployment.Application;
using System.Windows;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker;

internal class AutoUpdater
{
	private Action userNotification = delegate
	{
		DispatcherMessageBox.Show("新しいバージョンを検出しました。" + Environment.NewLine + "更新するには再起動する必要があります。", "確認", MessageBoxButton.OK, MessageBoxImage.Question);
	};

	private ApplicationDeployment AppDeployment { get; set; }

	public AutoUpdater()
	{
		AppDeployment = ApplicationDeployment.CurrentDeployment;
		AppDeployment.CheckForUpdateCompleted += onCheckForUpdateCompleted;
		AppDeployment.UpdateCompleted += onUpdateCompleted;
	}

	private void onUpdateCompleted(object sender, AsyncCompletedEventArgs e)
	{
		if (!e.Cancelled && e.Error == null)
		{
			try
			{
				userNotification?.Invoke();
			}
			catch
			{
			}
		}
	}

	private void onCheckForUpdateCompleted(object sender, CheckForUpdateCompletedEventArgs e)
	{
		if (e.Error != null || e.Cancelled || !e.UpdateAvailable)
		{
			return;
		}
		try
		{
			Version currentVersion = ApplicationDeployment.CurrentDeployment.CurrentVersion;
			if (e.AvailableVersion.Major == currentVersion.Major && e.AvailableVersion.Minor == currentVersion.Minor && e.AvailableVersion.Build == currentVersion.Build)
			{
				userNotification = null;
			}
		}
		catch
		{
		}
		AppDeployment.UpdateAsync();
	}

	public void Execute()
	{
		if (ApplicationDeployment.IsNetworkDeployed)
		{
			AppDeployment.CheckForUpdateAsync();
		}
	}
}

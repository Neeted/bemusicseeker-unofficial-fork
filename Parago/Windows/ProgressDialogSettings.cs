namespace Parago.Windows;

public class ProgressDialogSettings
{
	public static ProgressDialogSettings WithLabelOnly = new ProgressDialogSettings(showSubLabel: false, showCancelButton: false, showProgressBarIndeterminate: true);

	public static ProgressDialogSettings WithSubLabel = new ProgressDialogSettings(showSubLabel: true, showCancelButton: false, showProgressBarIndeterminate: true);

	public static ProgressDialogSettings WithSubLabelAndCancel = new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: true);

	public bool ShowSubLabel { get; set; }

	public bool ShowCancelButton { get; set; }

	public bool ShowProgressBarIndeterminate { get; set; }

	public ProgressDialogSettings()
	{
		ShowSubLabel = false;
		ShowCancelButton = false;
		ShowProgressBarIndeterminate = true;
	}

	public ProgressDialogSettings(bool showSubLabel, bool showCancelButton, bool showProgressBarIndeterminate)
	{
		ShowSubLabel = showSubLabel;
		ShowCancelButton = showCancelButton;
		ShowProgressBarIndeterminate = showProgressBarIndeterminate;
	}
}

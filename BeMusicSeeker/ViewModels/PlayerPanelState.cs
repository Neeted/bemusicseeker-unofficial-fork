using System;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Identifies the player-panel presentation selected by the main window and playback owner.
/// </summary>
[Flags]
public enum PlayerPanelState
{
    TITLE_LARGE = 0,
    TITLE_SMALL = 1,
    BMS_PLAYER = 2,
}

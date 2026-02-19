using System;

namespace Ribbit.Media;

[Flags]
public enum PlayWith
{
	DEFAULT = 0,
	RESTART = 2,
	MUTE = 4,
	PAUSE = 8
}

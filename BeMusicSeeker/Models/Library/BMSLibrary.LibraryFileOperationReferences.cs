using System;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    private static string GetDisplayedExceptionMessage(Exception exception)
    {
        return DisplayedExceptionMessage.Format(exception);
    }

}

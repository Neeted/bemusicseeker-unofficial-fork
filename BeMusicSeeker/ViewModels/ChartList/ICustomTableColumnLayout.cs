using System.Windows;

namespace BeMusicSeeker.ViewModels;

public interface ICustomTableColumnLayout
{
    int Width { get; set; }

    int DisplayIndex { get; set; }

    Visibility Visibility { get; set; }
}

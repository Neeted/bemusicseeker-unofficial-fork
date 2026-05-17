using System;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class playingBmsInfoConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length != 15)
        {
            return string.Empty;
        }
        try
        {
            TimeSpan timeSpan = (TimeSpan)values[0];
            TimeSpan timeSpan2 = (TimeSpan)values[1];
            TimeSpan timeSpan3 = (TimeSpan)values[2];
            int num = (int)values[3];
            int num2 = (int)values[4];
            int num3 = (int)values[5];
            int num4 = (int)values[6];
            int num5 = (int)values[7];
            int num6 = (int)values[8];
            int num7 = (int)values[9];
            _ = (double)values[10];
            int num8 = (int)values[11];
            int num9 = (int)values[12];
            int num10 = (int)values[13];
            int num11 = (int)values[14];
            string text = ((!(timeSpan.TotalHours > 1.0)) ? timeSpan.ToString("mm\\:ss\\.fff") : ((timeSpan.TotalDays > 1.0) ? timeSpan.ToString("d\\:hh\\:mm\\:ss") : timeSpan.ToString("hh\\:mm\\:ss")));
            string text2 = ((!(timeSpan2.TotalHours > 1.0)) ? timeSpan2.ToString("mm\\:ss\\.fff") : ((timeSpan2.TotalDays > 1.0) ? timeSpan2.ToString("d\\:hh\\:mm\\:ss") : timeSpan2.ToString("hh\\:mm\\:ss")));
            timeSpan3.ToString("mm\\:ss\\.fff");
            return "BPM   " + ((timeSpan3 == TimeSpan.Zero) ? num5.ToString().PadLeft(4, ' ') : "STOP") + ((num6 == num7) ? string.Empty : ("(" + num6 + "-" + num7 + ") ")) + Environment.NewLine + "Meas   " + num10.ToString().PadLeft(3, ' ') + "/" + num11.ToString() + Environment.NewLine + "Combo " + num8.ToString().PadLeft(4, ' ') + "/" + num9 + Environment.NewLine + "Voices " + num.ToString().PadLeft(3, ' ') + "   - " + num2.ToString().PadLeft(3, ' ') + Environment.NewLine + "Notes  " + num3.ToString().PadLeft(3, ' ') + "/s - " + num4.ToString().PadLeft(3, ' ') + "/s" + Environment.NewLine + "Music   " + text2 + Environment.NewLine + "BMS     " + text;
        }
        catch
        {
            return string.Empty;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

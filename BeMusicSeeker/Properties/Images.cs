using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace BeMusicSeeker.Properties;

public class Images
{
    private static ResourceManager resourceMan;
    private static CultureInfo resourceCulture;

    internal Images()
    {
    }

    public static ResourceManager ResourceManager
    {
        get
        {
            if (object.ReferenceEquals(resourceMan, null))
            {
                var temp = new ResourceManager("BeMusicSeeker.Properties.Images", typeof(Images).Assembly);
                resourceMan = temp;
            }
            return resourceMan;
        }
    }

    public static CultureInfo Culture
    {
        get
        {
            return resourceCulture;
        }
        set
        {
            resourceCulture = value;
        }
    }

    public static Icon imageres_8 => (Icon)ResourceManager.GetObject("imageres_8", resourceCulture);
    public static Icon imageres_18 => (Icon)ResourceManager.GetObject("imageres_18", resourceCulture);
    public static Icon imageres_108 => (Icon)ResourceManager.GetObject("imageres_108", resourceCulture);
    public static Icon imageres_131 => (Icon)ResourceManager.GetObject("imageres_131", resourceCulture);
    public static Icon imageres_137 => (Icon)ResourceManager.GetObject("imageres_137", resourceCulture);
    public static Icon imageres_180 => (Icon)ResourceManager.GetObject("imageres_180", resourceCulture);
    public static Icon imageres_1004 => (Icon)ResourceManager.GetObject("imageres_1004", resourceCulture);
    public static Icon imageres_5310 => (Icon)ResourceManager.GetObject("imageres_5310", resourceCulture);
    public static Icon imageres_5311 => (Icon)ResourceManager.GetObject("imageres_5311", resourceCulture);
    public static Icon imageres_5332 => (Icon)ResourceManager.GetObject("imageres_5332", resourceCulture);
    public static Icon imageres_5342 => (Icon)ResourceManager.GetObject("imageres_5342", resourceCulture);
}

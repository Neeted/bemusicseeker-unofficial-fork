using System;
using System.Globalization;

namespace BeMusicSeeker.Models.Utils;

[Serializable]
public class SerializableVersion : ICloneable, IComparable
{
    private int major;

    private int minor;

    private int build;

    private int revision;

    public int Major
    {
        get
        {
            return major;
        }
        set
        {
            major = value;
        }
    }

    public int Minor
    {
        get
        {
            return minor;
        }
        set
        {
            minor = value;
        }
    }

    public int Build
    {
        get
        {
            return build;
        }
        set
        {
            build = value;
        }
    }

    public int Revision
    {
        get
        {
            return revision;
        }
        set
        {
            revision = value;
        }
    }

    public SerializableVersion()
    {
        build = -1;
        revision = -1;
        major = 0;
        minor = 0;
    }

    public SerializableVersion(string version)
    {
        build = -1;
        revision = -1;
        if (version == null)
        {
            throw new ArgumentNullException("version");
        }
        char[] separator = ['.'];
        string[] array = version.Split(separator);
        int num = array.Length;
        if (num < 2 || num > 4)
        {
            throw new ArgumentException("Arg_VersionString");
        }
        major = int.Parse(array[0], CultureInfo.InvariantCulture);
        if (major < 0)
        {
            throw new ArgumentOutOfRangeException("version", "ArgumentOutOfRange_Version");
        }
        minor = int.Parse(array[1], CultureInfo.InvariantCulture);
        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException("version", "ArgumentOutOfRange_Version");
        }
        num -= 2;
        if (num <= 0)
        {
            return;
        }
        build = int.Parse(array[2], CultureInfo.InvariantCulture);
        if (build < 0)
        {
            throw new ArgumentOutOfRangeException("build", "ArgumentOutOfRange_Version");
        }
        num--;
        if (num > 0)
        {
            revision = int.Parse(array[3], CultureInfo.InvariantCulture);
            if (revision < 0)
            {
                throw new ArgumentOutOfRangeException("revision", "ArgumentOutOfRange_Version");
            }
        }
    }

    public SerializableVersion(int major, int minor)
    {
        build = -1;
        revision = -1;
        if (major < 0)
        {
            throw new ArgumentOutOfRangeException("major", "ArgumentOutOfRange_Version");
        }
        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException("minor", "ArgumentOutOfRange_Version");
        }
        this.major = major;
        this.minor = minor;
        this.major = major;
    }

    public SerializableVersion(int major, int minor, int build)
    {
        this.build = -1;
        revision = -1;
        if (major < 0)
        {
            throw new ArgumentOutOfRangeException("major", "ArgumentOutOfRange_Version");
        }
        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException("minor", "ArgumentOutOfRange_Version");
        }
        if (build < 0)
        {
            throw new ArgumentOutOfRangeException("build", "ArgumentOutOfRange_Version");
        }
        this.major = major;
        this.minor = minor;
        this.build = build;
    }

    public SerializableVersion(int major, int minor, int build, int revision)
    {
        this.build = -1;
        this.revision = -1;
        if (major < 0)
        {
            throw new ArgumentOutOfRangeException("major", "ArgumentOutOfRange_Version");
        }
        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException("minor", "ArgumentOutOfRange_Version");
        }
        if (build < 0)
        {
            throw new ArgumentOutOfRangeException("build", "ArgumentOutOfRange_Version");
        }
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException("revision", "ArgumentOutOfRange_Version");
        }
        this.major = major;
        this.minor = minor;
        this.build = build;
        this.revision = revision;
    }

    public SerializableVersion(Version version)
        : this(version.Major, version.Minor, version.Build, version.Revision)
    {
    }

    public object Clone()
    {
        return new SerializableVersion
        {
            major = major,
            minor = minor,
            build = build,
            revision = revision
        };
    }

    public int CompareTo(object version)
    {
        if (version == null)
        {
            return 1;
        }
        if (version is not SerializableVersion)
        {
            throw new ArgumentException("Arg_MustBeVersion");
        }
        var serializableVersion = (SerializableVersion)version;
        if (major != serializableVersion.Major)
        {
            if (major > serializableVersion.Major)
            {
                return 1;
            }
            return -1;
        }
        if (minor != serializableVersion.Minor)
        {
            if (minor > serializableVersion.Minor)
            {
                return 1;
            }
            return -1;
        }
        if (build != serializableVersion.Build)
        {
            if (build > serializableVersion.Build)
            {
                return 1;
            }
            return -1;
        }
        if (revision == serializableVersion.Revision)
        {
            return 0;
        }
        if (revision > serializableVersion.Revision)
        {
            return 1;
        }
        return -1;
    }

    public override bool Equals(object obj)
    {
        if (obj == null || obj is not SerializableVersion)
        {
            return false;
        }
        var serializableVersion = (SerializableVersion)obj;
        if (major == serializableVersion.Major && minor == serializableVersion.Minor && build == serializableVersion.Build && revision == serializableVersion.Revision)
        {
            return true;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return 0 | ((major & 0xF) << 28) | ((minor & 0xFF) << 20) | ((build & 0xFF) << 12) | (revision & 0xFFF);
    }

    public static bool operator ==(SerializableVersion v1, SerializableVersion v2)
    {
        return v1?.Equals(v2) ?? (v2 is null);
    }

    public static bool operator >(SerializableVersion v1, SerializableVersion v2)
    {
        return v2 < v1;
    }

    public static bool operator >=(SerializableVersion v1, SerializableVersion v2)
    {
        return v2 <= v1;
    }

    public static bool operator !=(SerializableVersion v1, SerializableVersion v2)
    {
        return !(v1 == v2);
    }

    public static bool operator <(SerializableVersion v1, SerializableVersion v2)
    {
        if (v1 == null)
        {
            throw new ArgumentNullException("v1");
        }
        return v1.CompareTo(v2) < 0;
    }

    public static bool operator <=(SerializableVersion v1, SerializableVersion v2)
    {
        if (v1 == null)
        {
            throw new ArgumentNullException("v1");
        }
        return v1.CompareTo(v2) <= 0;
    }

    public override string ToString()
    {
        if (build == -1)
        {
            return ToString(2);
        }
        if (revision == -1)
        {
            return ToString(3);
        }
        return ToString(4);
    }

    public string ToString(int fieldCount)
    {
        switch (fieldCount)
        {
            case 0:
                return string.Empty;
            case 1:
                return major.ToString();
            case 2:
                return major + "." + minor;
            default:
                if (build == -1)
                {
                    throw new ArgumentException(string.Format("ArgumentOutOfRange_Bounds_Lower_Upper {0},{1}", "0", "2"), "fieldCount");
                }
                if (fieldCount == 3)
                {
                    return major + "." + minor + "." + build;
                }
                if (revision == -1)
                {
                    throw new ArgumentException(string.Format("ArgumentOutOfRange_Bounds_Lower_Upper {0},{1}", "0", "3"), "fieldCount");
                }
                if (fieldCount == 4)
                {
                    return major + "." + minor + "." + build + "." + revision;
                }
                throw new ArgumentException(string.Format("ArgumentOutOfRange_Bounds_Lower_Upper {0},{1}", "0", "4"), "fieldCount");
        }
    }
}

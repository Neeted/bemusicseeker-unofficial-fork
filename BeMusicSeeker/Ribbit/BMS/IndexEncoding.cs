using System;

namespace Ribbit.BMS;

[Flags]
public enum IndexEncoding
{
    Unknown = 0,
    Base16 = 1,
    Base36 = 2,
    Base64 = 4
}

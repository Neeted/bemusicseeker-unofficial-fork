using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

public class LR2IRData : LR2SongDBExtended.ir_data
{
    public int score => base.pg * 2 + base.gr;

    public LR2IRData()
    {
    }

    public LR2IRData(string md5)
    {
        base.hash = md5;
    }
}

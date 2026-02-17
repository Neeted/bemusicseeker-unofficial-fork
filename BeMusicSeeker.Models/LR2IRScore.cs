using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

public class LR2IRScore : LR2SongDBExtended.ir_score
{
	public int score => base.pg * 2 + base.gr;

	public LR2IRScore()
	{
	}

	public LR2IRScore(string md5)
	{
		hash = md5;
	}
}

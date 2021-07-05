using System.Collections.Generic;

namespace ServerCharacters
{
	public class PlayerTemplate
	{
		public Dictionary<string, float> skills { get; set; } = new();
		public Dictionary<string, int> items { get; set; } = new();
		public Position? spawn  { get; set; }

		public class Position
		{
			public int x { get; set; }
			public int y { get; set; }
			public int z { get; set; }
		}
	}
	
}

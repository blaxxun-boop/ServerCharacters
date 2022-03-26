using System.Collections.Generic;
using JetBrains.Annotations;

namespace ServerCharacters;

[PublicAPI]
public class PlayerTemplate
{
	public Dictionary<string, float> skills { get; set; } = new();
	public Dictionary<string, int> items { get; set; } = new();
	public Position? spawn { get; set; }

	[PublicAPI]
	public class Position
	{
		public int x { get; set; }
		public int y { get; set; }
		public int z { get; set; }
	}
}

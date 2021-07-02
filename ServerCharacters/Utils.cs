using BepInEx.Configuration;

namespace ServerCharacters
{
	public static class Utils
	{
		public static bool GetToggle(this ConfigEntry<Toggle> toggle)
		{
			return toggle.Value == Toggle.On;
		}
	}
}

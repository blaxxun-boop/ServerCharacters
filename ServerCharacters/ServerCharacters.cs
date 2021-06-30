using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;

namespace ServerCharacters
{
	[BepInPlugin(ModGUID, ModName, ModVersion)]
	public class ServerCharacters : BaseUnityPlugin
	{
		private const string ModName = "Server Characters";
		private const string ModVersion = "1.0";
		private const string ModGUID = "org.bepinex.plugins.servercharacters";

		private readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName };
		
		private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
		{
			ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

			SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
			syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

			return configEntry;
		}

		private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);
		
		public void Awake()
		{
			Assembly assembly = Assembly.GetExecutingAssembly();
			Harmony harmony = new(ModGUID);
			harmony.PatchAll(assembly);
		}
	}
}

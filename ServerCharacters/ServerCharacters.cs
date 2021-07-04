using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace ServerCharacters
{
	[BepInPlugin(ModGUID, ModName, ModVersion)]
	public class ServerCharacters : BaseUnityPlugin
	{
		private const string ModName = "Server Characters";
		private const string ModVersion = "1.0";
		private const string ModGUID = "org.bepinex.plugins.servercharacters";

		private float fixedUpdateCount = 0;
		private int tickCount = int.MaxValue;
		public static int monotonicCounter = 0;

		public const int MaintenanceDisconnectMagic = 987345987;

		private readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName };

		private static ConfigEntry<Toggle> serverConfigLocked = null!;
		public static ConfigEntry<Toggle> maintenanceMode = null!;
		private static ConfigEntry<int> maintenanceTimer = null!;
		public static ConfigEntry<int> backupsToKeep = null!;
		public static ConfigEntry<int> autoSaveInterval = null!;

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
			serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.Off, "If on, the configuration is locked and can be changed by server admins only.");
			configSync.AddLockingConfigEntry(serverConfigLocked);
			maintenanceMode = config("1 - General", "Maintenance Mode", Toggle.Off, "If set to on, a timer will start. If the timer elapses, all non-admins will be disconnected, the world will be saved and only admins will be able to connect to the server, until maintenance mode is toggled to off.");
			maintenanceMode.SettingChanged += toggleMaintenanceMode;
			maintenanceTimer = config("1 - General", "Maintenance Timer", 300, new ConfigDescription("Time in seconds that has to pass, before the maintenance mode becomes active.", new AcceptableValueRange<int>(10, 1800)));
			backupsToKeep = config("1 - General", "Number of backups to keep", 5, new ConfigDescription("Sets the number of backups that should be stored for each character.", new AcceptableValueRange<int>(0, 15)));
			autoSaveInterval = config("1 - General", "Auto save interval", 20, new ConfigDescription("Minutes between auto saves of characters and the world.", new AcceptableValueRange<int>(1, 30)));

			Assembly assembly = Assembly.GetExecutingAssembly();
			Harmony harmony = new(ModGUID);
			harmony.PatchAll(assembly);

			FileSystemWatcher watcher = new(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "maintenance");
			watcher.Created += maintenanceFileEvent;
			watcher.Deleted += maintenanceFileEvent;
			watcher.IncludeSubdirectories = true;
			watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
			watcher.EnableRaisingEvents = true;
		}

		private static void maintenanceFileEvent(object s, EventArgs e)
		{
			Toggle maintenance = File.Exists(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)! + Path.DirectorySeparatorChar + "maintenance") ? Toggle.On : Toggle.Off;
			SyncedConfigEntry<Toggle> cfg = ConfigSync.ConfigData(maintenanceMode)!;
			if (cfg.LocalBaseValue == null)
			{
				maintenanceMode.Value = maintenance;
			}
			else
			{
				cfg.LocalBaseValue = maintenance;
			}
		}

		private void toggleMaintenanceMode(object s, EventArgs e)
		{
			if (maintenanceMode.GetToggle())
			{
				string text = $"Maintenance mode enabled. All non-admins will be disconnected in {maintenanceTimer.Value} seconds";
				Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);

				tickCount = maintenanceTimer.Value;

				if (configSync.IsSourceOfTruth)
				{
					File.Create(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)! + Path.DirectorySeparatorChar + "maintenance");
				}
			}
			else
			{
				if (tickCount <= maintenanceTimer.Value)
				{
					const string text = "Maintenance aborted";
					Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);

					tickCount = int.MaxValue;
				}

				if (configSync.IsSourceOfTruth)
				{
					File.Delete(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)! + Path.DirectorySeparatorChar + "maintenance");
				}
			}
		}

		private void FixedUpdate()
		{
			const float timerInterval = 1f;
			fixedUpdateCount += Time.fixedDeltaTime;
			if ((double)fixedUpdateCount < timerInterval)
			{
				return;
			}

			if (tickCount <= 0)
			{
				if (ZNet.instance?.IsServer() == true)
				{
					foreach (ZNetPeer peer in ZNet.instance.GetPeers())
					{
						if (!ZNet.instance.m_adminList.Contains(peer.m_rpc.GetSocket().GetHostName()))
						{
							ZNet.instance.InternalKick(peer);
						}
					}

					ZNet.instance.ConsoleSave();
				}

				tickCount = int.MaxValue;
			}

			fixedUpdateCount -= timerInterval;
			--tickCount;
			++monotonicCounter;
		}
	}
}

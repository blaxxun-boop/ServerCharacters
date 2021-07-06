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

		private static ServerCharacters selfReference;

		private float fixedUpdateCount = 0;
		private int tickCount = int.MaxValue;
		public static int monotonicCounter = 0;

		public const int MaintenanceDisconnectMagic = 987345987;
		public const int CharacterNameDisconnectMagic = 498209834;

		private static readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName };

		private static ConfigEntry<Toggle> serverConfigLocked = null!;
		public static ConfigEntry<Toggle> maintenanceMode = null!;
		private static ConfigEntry<int> maintenanceTimer = null!;
		public static ConfigEntry<int> backupsToKeep = null!;
		public static ConfigEntry<int> autoSaveInterval = null!;
		public static ConfigEntry<string> webhookURL = null!;
		public static ConfigEntry<string> webhookUsername = null!;
		private static ConfigEntry<string> maintenanceEnabledText = null!;
		private static ConfigEntry<string> maintenanceFinishedText = null!;
		private static ConfigEntry<string> maintenanceAbortedText = null!;
		private static ConfigEntry<string> maintenanceStartedText = null!;

		public static readonly CustomSyncedValue<string> playerTemplate = new(configSync, "PlayerTemplate", readCharacterTemplate());

		private static string pluginDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

		private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
		{
			ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

			SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
			syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

			return configEntry;
		}

		private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

		private class ConfigurationManagerAttributes
		{
			public bool? Browsable = false;
		}

		public void Awake()
		{
			selfReference = this;
			serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.Off, "If on, the configuration is locked and can be changed by server admins only.");
			configSync.AddLockingConfigEntry(serverConfigLocked);
			maintenanceMode = config("1 - General", "Maintenance Mode", Toggle.Off, "If set to on, a timer will start. If the timer elapses, all non-admins will be disconnected, the world will be saved and only admins will be able to connect to the server, until maintenance mode is toggled to off.");
			maintenanceMode.SettingChanged += toggleMaintenanceMode;
			maintenanceTimer = config("1 - General", "Maintenance Timer", 300, new ConfigDescription("Time in seconds that has to pass, before the maintenance mode becomes active.", new AcceptableValueRange<int>(10, 1800)));
			backupsToKeep = config("1 - General", "Number of backups to keep", 5, new ConfigDescription("Sets the number of backups that should be stored for each character.", new AcceptableValueRange<int>(0, 15)));
			autoSaveInterval = config("1 - General", "Auto save interval", 20, new ConfigDescription("Minutes between auto saves of characters and the world.", new AcceptableValueRange<int>(1, 30)));
			webhookURL = config("1 - General", "Discord Webhook URL", "", new ConfigDescription("Discord API endpoint to announce maintenance.", null, new ConfigurationManagerAttributes()), false);
			webhookUsername = config("1 - General", "Username to use for Discord", "Maintenance Bot", new ConfigDescription("Username to be used for maintenance related posts to Discord.", null, new ConfigurationManagerAttributes()), false);
			maintenanceEnabledText = config("1 - General", "Maintenance enabled text", "Maintenance mode enabled. All non-admins will be disconnected in {time}.", new ConfigDescription("Message to be posted to Discord, when the maintenance mode has been toggled to 'On'. Leave empty to not post anything. Use {time} for the time until the maintenance starts.", null, new ConfigurationManagerAttributes()), false);
			maintenanceFinishedText = config("1 - General", "Maintenance finished text", "Maintenance has been disabled and the server is back online. Have fun!", new ConfigDescription("Message to be posted to Discord, when the maintenance mode has been toggled to 'Off'. Leave empty to not post anything.", null, new ConfigurationManagerAttributes()), false);
			maintenanceAbortedText = config("1 - General", "Maintenance aborted text", "Maintenance has been aborted.", new ConfigDescription("Message to be posted to Discord, when the maintenance has been aborted. Leave empty to not post anything.", null, new ConfigurationManagerAttributes()), false);
			maintenanceStartedText = config("1 - General", "Maintenance started text", "Maintenance has started and players will be unable to connect.", new ConfigDescription("Message to be posted to Discord, when the maintenance has begun. Leave empty to not post anything.", null, new ConfigurationManagerAttributes()), false);

			Assembly assembly = Assembly.GetExecutingAssembly();
			Harmony harmony = new(ModGUID);
			harmony.PatchAll(assembly);

			FileSystemWatcher maintenanceFileWatcher = new(pluginDir, "maintenance");
			maintenanceFileWatcher.Created += maintenanceFileEvent;
			maintenanceFileWatcher.Deleted += maintenanceFileEvent;
			maintenanceFileWatcher.IncludeSubdirectories = true;
			maintenanceFileWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
			maintenanceFileWatcher.EnableRaisingEvents = true;
			
			FileSystemWatcher characterTemplateWatcher = new(pluginDir, "CharacterTemplate.yml");
			characterTemplateWatcher.Created += templateFileEvent;
			characterTemplateWatcher.Changed += templateFileEvent;
			characterTemplateWatcher.Renamed += templateFileEvent;
			characterTemplateWatcher.Deleted += templateFileEvent;
			characterTemplateWatcher.IncludeSubdirectories = true;
			characterTemplateWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
			characterTemplateWatcher.EnableRaisingEvents = true;
		}

		private static void maintenanceFileEvent(object s, EventArgs e)
		{
			Toggle maintenance = File.Exists(pluginDir + Path.DirectorySeparatorChar + "maintenance") ? Toggle.On : Toggle.Off;
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

		private static void templateFileEvent(object s, EventArgs e) => playerTemplate.AssignLocalValue(readCharacterTemplate());

		private static string readCharacterTemplate()
		{
			string templatePath = pluginDir + Path.DirectorySeparatorChar + "CharacterTemplate.yml";
			return File.Exists(templatePath) ? File.ReadAllText(templatePath) : "";
		}

		private void toggleMaintenanceMode(object s, EventArgs e)
		{
			if (maintenanceMode.GetToggle())
			{
				string text = $"Maintenance mode enabled. All non-admins will be disconnected in {Utils.getHumanFriendlyTime(maintenanceTimer.Value)}.";
				Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
				Log(text);

				tickCount = maintenanceTimer.Value;

				if (configSync.IsSourceOfTruth)
				{
					File.Create(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)! + Path.DirectorySeparatorChar + "maintenance");
					Utils.PostToDiscord(maintenanceEnabledText.Value.Replace("{time}", Utils.getHumanFriendlyTime(maintenanceTimer.Value)));
				}
			}
			else
			{
				if (configSync.IsSourceOfTruth)
				{
					File.Delete(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)! + Path.DirectorySeparatorChar + "maintenance");

					Utils.PostToDiscord(tickCount <= maintenanceTimer.Value ? maintenanceAbortedText.Value : maintenanceFinishedText.Value);
				}
				
				if (tickCount <= maintenanceTimer.Value)
				{
					const string text = "Maintenance aborted";
					Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
					Log(text);

					tickCount = int.MaxValue;
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
							Log($"disconnected client {peer.m_rpc.GetSocket().GetHostName()}");
						}
					}

					ZNet.instance.ConsoleSave();
					Log("saved world");
					Utils.PostToDiscord(maintenanceStartedText.Value);
				}

				tickCount = int.MaxValue;
			}

			fixedUpdateCount -= timerInterval;
			--tickCount;
			++monotonicCounter;
		}

		public static void Log(string message)
        {
			selfReference.Logger.LogMessage(message);
        }
	}
}

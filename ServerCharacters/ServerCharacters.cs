using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;

namespace ServerCharacters;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
public class ServerCharacters : BaseUnityPlugin
{
	private const string ModName = "Server Characters";
	private const string ModVersion = "1.4.3";
	private const string ModGUID = "org.bepinex.plugins.servercharacters";

	public static ServerCharacters selfReference = null!;
	public static ManualLogSource logger => selfReference.Logger;
	private static readonly Harmony harmony = new(ModGUID);

	private float fixedUpdateCount = 0;
	public int tickCount = int.MaxValue;
	public static int monotonicCounter = 0;

	public const int MaintenanceDisconnectMagic = 987345987;
	public const int CharacterNameDisconnectMagic = 498209834;
	public const int SingleCharacterModeDisconnectMagic = 845979243;

	public static readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = "1.4.3" };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	public static ConfigEntry<Toggle> singleCharacterMode = null!;
	public static ConfigEntry<Toggle> backupOnlyMode = null!;
	public static ConfigEntry<Toggle> hardcoreMode = null!;
	public static ConfigEntry<Toggle> maintenanceMode = null!;
	private static ConfigEntry<int> maintenanceTimer = null!;
	public static ConfigEntry<int> backupsToKeep = null!;
	public static ConfigEntry<int> autoSaveInterval = null!;
	public static ConfigEntry<int> afkKickTimer = null!;
	public static ConfigEntry<string> webhookURL = null!;
	private static ConfigEntry<string> webhookUsernameMaintenance = null!;
	private static ConfigEntry<string> maintenanceEnabledText = null!;
	private static ConfigEntry<string> maintenanceFinishedText = null!;
	private static ConfigEntry<string> maintenanceAbortedText = null!;
	private static ConfigEntry<string> maintenanceStartedText = null!;
	public static ConfigEntry<string> loginMessage = null!;
	public static ConfigEntry<string> firstLoginMessage = null!;
	public static ConfigEntry<Toggle> postFirstLoginToWebhook = null!;
	public static ConfigEntry<string> webhookUsernameOther = null!;
	public static ConfigEntry<string> serverKey = null!;
	public static ConfigEntry<string> serverListenAddress = null!;
	public static ConfigEntry<Intro> newCharacterIntro = null!;
	public static ConfigEntry<Toggle> storePoison = null!;

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
		[UsedImplicitly]
		public bool? Browsable = false;
	}

	public void Awake()
	{
		selfReference = this;
		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		afkKickTimer = config("1 - General", "AFK Kick Timer", 0, new ConfigDescription("Automatically kicks players, if they haven't moved at all in the configured time. In minutes. 0 is disabled.", new AcceptableValueRange<int>(0, 30)));
		webhookURL = config("1 - General", "Discord Webhook URL", "", new ConfigDescription("Discord API endpoint to announce maintenance.", null, new ConfigurationManagerAttributes()), false);
		loginMessage = config("1 - General", "Login Message", "I have arrived!", new ConfigDescription("Message to shout on login. Leave empty to not shout anything."));
		webhookUsernameOther = config("1 - General", "Discord Username Other", "Server Characters", new ConfigDescription("Username to be used for non-maintenance related posts to Discord.", null, new ConfigurationManagerAttributes()), false);

		hardcoreMode = config("2 - Save Files", "Hardcore mode", Toggle.Off, "If set to on, players will be kicked from the server and their save file on the server will be deleted, if they die.");
		singleCharacterMode = config("2 - Save Files", "Single Character Mode", Toggle.Off, "If set to on, each SteamID / Xbox ID can create one character only on this server. Has no effect for admins.");
		backupOnlyMode = config("2 - Save Files", "Backup only mode", Toggle.Off, "Enabling this will not enforce the server profile anymore. DO NOT ENABLE THIS IF YOU DON'T KNOW EXACTLY WHAT YOU ARE DOING!");
		backupsToKeep = config("2 - Save Files", "Number of backups to keep", 25, new ConfigDescription("Sets the number of backups that should be stored for each character.", new AcceptableValueRange<int>(1, 50)));
		autoSaveInterval = config("2 - Save Files", "Auto save interval", 30, new ConfigDescription("Minutes between auto saves of characters and the world.", new AcceptableValueRange<int>(1, 120)));
		autoSaveInterval.SettingChanged += (_, _) => Game.m_saveInterval = autoSaveInterval.Value * 60;
		storePoison = config("2 - Save Files", "Store poison debuff", Toggle.On, new ConfigDescription("If on, poison debuffs are stored in the save file on logout and applied on login, to prevent users from logging out if they are poisoned, to clear the debuff."));

		maintenanceMode = config("3 - Maintenance", "Maintenance Mode", Toggle.Off, "If set to on, a timer will start. If the timer elapses, all non-admins will be disconnected, the world will be saved and only admins will be able to connect to the server, until maintenance mode is toggled to off.");
		maintenanceMode.SettingChanged += toggleMaintenanceMode;
		maintenanceTimer = config("3 - Maintenance", "Maintenance Timer", 300, new ConfigDescription("Time in seconds that has to pass, before the maintenance mode becomes active.", new AcceptableValueRange<int>(10, 1800)));
		webhookUsernameMaintenance = config("3 - Maintenance", "Discord Username Maintenance", "Maintenance Bot", new ConfigDescription("Username to be used for maintenance related posts to Discord.", null, new ConfigurationManagerAttributes()), false);
		maintenanceEnabledText = config("3 - Maintenance", "Maintenance enabled text", "Maintenance mode enabled. All non-admins will be disconnected in {time}.", new ConfigDescription("Message to be posted to Discord, when the maintenance mode has been toggled to 'On'. Leave empty to not post anything. Use {time} for the time until the maintenance starts.", null, new ConfigurationManagerAttributes()), false);
		maintenanceFinishedText = config("3 - Maintenance", "Maintenance finished text", "Maintenance has been disabled and the server is back online. Have fun!", new ConfigDescription("Message to be posted to Discord, when the maintenance mode has been toggled to 'Off'. Leave empty to not post anything.", null, new ConfigurationManagerAttributes()), false);
		maintenanceAbortedText = config("3 - Maintenance", "Maintenance aborted text", "Maintenance has been aborted.", new ConfigDescription("Message to be posted to Discord, when the maintenance has been aborted. Leave empty to not post anything.", null, new ConfigurationManagerAttributes()), false);
		maintenanceStartedText = config("3 - Maintenance", "Maintenance started text", "Maintenance has started and players will be unable to connect.", new ConfigDescription("Message to be posted to Discord, when the maintenance has begun. Leave empty to not post anything.", null, new ConfigurationManagerAttributes()), false);
		
		firstLoginMessage = config("3 - First Login", "First Login Message", "A new player logged in for the first time: {name}", new ConfigDescription("Message to display if a player logs in for the very first time. Leave empty to not display anything."));
		newCharacterIntro = config("3 - First Login", "Intro", Intro.ValkyrieAndIntro, new ConfigDescription("Sets the kind of intro new characters will get."));
		postFirstLoginToWebhook = config("3 - First Login", "First Login Webhook", Toggle.Off, new ConfigDescription("If on, the first login message is posted to the webhook as well.", null, new ConfigurationManagerAttributes()), false);

		serverKey = config("4 - Other", "Server key", "", new ConfigDescription("DO NOT TOUCH THIS! DO NOT SHARE THIS! Encryption key used for emergency profile backups. DO NOT SHARE THIS! DO NOT TOUCH THIS!", null, new ConfigurationManagerAttributes()), false);
		serverListenAddress = config("4 - Other", "Webinterface listen address", "127.0.0.1:5982", new ConfigDescription("The address the webinterface API should listen on. Clear this value, if you don't use the webinterface.", null, new ConfigurationManagerAttributes()), false);

		Assembly assembly = Assembly.GetExecutingAssembly();
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

		ServerSide.generateServerKey();

		harmony.Patch(AccessTools.DeclaredMethod(typeof(FejdStartup), nameof(Awake)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ServerCharacters), nameof(Initialize))));
	}

	public static void Initialize()
	{
		harmony.Unpatch(AccessTools.DeclaredMethod(typeof(FejdStartup), nameof(Awake)), HarmonyPatchType.Postfix, harmony.Id);

		if (!serverListenAddress.Value.IsNullOrWhiteSpace())
		{
			WebInterfaceAPI.StartServer();
		}

		Directory.CreateDirectory(Utils.CharacterSavePath);

		string legacyPath = PlayerProfile.GetCharacterFolderPath(FileHelpers.FileSource.Legacy);
		if (Directory.Exists(legacyPath))
		{
			foreach (string s in Directory.GetFiles(legacyPath))
			{
				FileInfo file = new(s);
				if (Utils.IsServerCharactersFilePattern(file.Name) || file.Name == "backups")
				{
					Directory.Move(file.FullName, Utils.CharacterSavePath + Path.DirectorySeparatorChar + file.Name);
				}
			}
		}

		foreach (string s in Directory.GetFiles(Utils.CharacterSavePath))
		{
			FileInfo file = new(s);
			if (file.Name.EndsWith(".fch", StringComparison.Ordinal) && Regex.IsMatch(file.Name.Split('_')[0], @"^\d+$"))
			{
				string newPath = file.DirectoryName + Path.DirectorySeparatorChar + "Steam_" + file.Name;
				file.MoveTo(newPath);
			}
		}

		foreach (string s in Directory.GetFiles(Utils.CharacterSavePath))
		{
			FileInfo file = new(s);
			if (Utils.IsServerCharactersFilePattern(file.Name))
			{
				Utils.ProfileName profileName = new();

				string[] parts = file.Name.Split('_');
				profileName.id = $"{parts[0]}_{parts[1]}";
				profileName.name = parts[2].Split('.')[0];
				PlayerProfile profile = new(file.Name.Replace(".fch", ""), FileHelpers.FileSource.Local);
				profile.Load();
				Utils.Cache.profiles[profileName] = profile;
			}
		}
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
			string text = maintenanceEnabledText.Value.Replace("{time}", Utils.getHumanFriendlyTime(maintenanceTimer.Value));
			Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
			Utils.Log(text);

			tickCount = maintenanceTimer.Value;

			WebInterfaceAPI.SendMaintenanceMessage(new Maintenance { startTime = DateTimeOffset.Now.ToUnixTimeSeconds() + tickCount, maintenanceActive = false });

			if (configSync.IsSourceOfTruth)
			{
				File.Create(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)! + Path.DirectorySeparatorChar + "maintenance");
				Utils.PostToDiscord(text, webhookUsernameMaintenance.Value);
			}
		}
		else
		{
			if (configSync.IsSourceOfTruth)
			{
				File.Delete(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)! + Path.DirectorySeparatorChar + "maintenance");
				Utils.PostToDiscord(tickCount <= maintenanceTimer.Value ? maintenanceAbortedText.Value : maintenanceFinishedText.Value, webhookUsernameMaintenance.Value);
				WebInterfaceAPI.SendMaintenanceMessage(new Maintenance { startTime = 0, maintenanceActive = false });
			}

			if (tickCount <= maintenanceTimer.Value)
			{
				const string text = "Maintenance aborted";
				Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
				Utils.Log(text);

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

		if (ZNet.instance?.IsServer() != true && ClientSide.serverCharacter)
		{
			ClientSide.snapShotProfile();
		}

		if (tickCount <= 0)
		{
			if (ZNet.instance?.IsServer() == true)
			{
				foreach (ZNetPeer peer in ZNet.instance.GetPeers())
				{
					if (!ZNet.instance.ListContainsId(ZNet.instance.m_adminList, peer.m_rpc.GetSocket().GetHostName()))
					{
						ZNet.instance.InternalKick(peer);
						Utils.Log($"Kicked non-admin client {Utils.GetPlayerID(peer.m_rpc.GetSocket().GetHostName())}, reason: Maintenance started.");
					}
				}

				ZNet.instance.ConsoleSave();
				Utils.Log("Maintenance started. World has been saved.");
				Utils.PostToDiscord(maintenanceStartedText.Value, webhookUsernameMaintenance.Value);
				WebInterfaceAPI.SendMaintenanceMessage(new Maintenance { startTime = DateTimeOffset.Now.ToUnixTimeSeconds(), maintenanceActive = true });
			}

			tickCount = int.MaxValue;
		}

		fixedUpdateCount -= timerInterval;
		--tickCount;
		++monotonicCounter;
	}
}

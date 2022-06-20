using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using BepInEx.Configuration;
using JetBrains.Annotations;
using UnityEngine;

namespace ServerCharacters;

public static class Utils
{
	public static bool GetToggle(this ConfigEntry<Toggle> toggle)
	{
		return toggle.Value == Toggle.On;
	}

	public static string getHumanFriendlyTime(int seconds)
	{
		TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);

		return (timeSpan.TotalMinutes >= 1 ? $"{(int)timeSpan.TotalMinutes} minute" + (timeSpan.TotalMinutes >= 2 ? "s" : "") + (timeSpan.Seconds != 0 ? " and " : "") : "") + (timeSpan.Seconds != 0 ? $"{timeSpan.Seconds} second" + (timeSpan.Seconds >= 2 ? "s" : "") : "");
	}

	public static void PostToDiscord(string content)
	{
		if (content == "" || ServerCharacters.webhookURL.Value == "")
		{
			return;
		}

		WebRequest discordAPI = WebRequest.Create(ServerCharacters.webhookURL.Value);
		discordAPI.Method = "POST";
		discordAPI.ContentType = "application/json";

		discordAPI.GetRequestStreamAsync().ContinueWith(t =>
		{
			static string escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

			using StreamWriter writer = new(t.Result);
			string json = @"{""content"":""" + escape(content) + @"""" + (ServerCharacters.webhookUsername.Value == "" ? "" : @", ""username"":""" + escape(ServerCharacters.webhookUsername.Value) + @"""") + "}";
			writer.WriteAsync(json).ContinueWith(_ => discordAPI.GetResponseAsync());
		});
	}

	public static void Log(string message)
	{
		ServerCharacters.logger.LogMessage(message);
	}

	public static readonly string CharacterSavePath = PlayerProfile.GetCharacterFolderPath(FileHelpers.FileSource.Local);

	public static bool IsServerCharactersFilePattern(string file) => file.Contains("_") && file.EndsWith(".fch", StringComparison.Ordinal) && !file.Contains("_backup_");

	public struct ProfileName
	{
		[UsedImplicitly] public string id;
		[UsedImplicitly] public string name;

		public static ProfileName fromPeer(ZNetPeer peer) => new() { id = peer.m_socket.GetHostName(), name = peer.m_playerName };
	}

	public static class Cache
	{
		public static readonly Dictionary<ProfileName, PlayerProfile> profiles = new();

		public static PlayerProfile loadProfile(ProfileName name)
		{
			if (!profiles.TryGetValue(name, out PlayerProfile profile))
			{
				profile = new PlayerProfile($"{name.id}_{name.name}", FileHelpers.FileSource.Local);
				profile.LoadPlayerFromDisk();
				profiles[name] = profile;
			}

			return profile;
		}
	}

	public static PlayerList GetPlayerListFromFiles()
	{
		PlayerList playerList = new();
		Dictionary<ProfileName, ZNet.PlayerInfo> playerInfos = ZNet.m_instance.m_players.ToDictionary(p => new ProfileName { id = p.m_host, name = p.m_name }, p => p);
		foreach (string s in Directory.GetFiles(CharacterSavePath))
		{
			FileInfo file = new(s);
			if (Utils.IsServerCharactersFilePattern(file.Name))
			{
				WebinterfacePlayer player = new();

				string[] parts = file.Name.Split('_');
				player.Id = parts[0];
				player.Name = parts[1].Split('.')[0];
				ProfileName profileName = new() { id = player.Id, name = player.Name };
				bool loggedIn = playerInfos.TryGetValue(profileName, out ZNet.PlayerInfo playerInfo);
				PlayerProfile profile = Cache.loadProfile(profileName);
				player.statistics = new WebinterfacePlayer.Statistics
				{
					lastTouch = loggedIn ? 0 : ((DateTimeOffset)file.LastWriteTimeUtc).ToUnixTimeSeconds(),
					Kills = profile.m_playerStats.m_kills,
					Deaths = profile.m_playerStats.m_deaths,
					Crafts = profile.m_playerStats.m_crafts,
					Builds = profile.m_playerStats.m_builds
				};
				Vector3 position = loggedIn ? ZNet.instance.GetPeerByHostName(playerInfo.m_host).m_refPos : profile.GetLogoutPoint();
				player.position = new WebinterfacePlayer.Position
				{
					X = position.x,
					Y = position.y,
					Z = position.z
				};

				playerList.playerLists.Add(player);
			}
		}

		return playerList;
	}
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ServerCharacters;

public static class ServerSide
{
	private static readonly Dictionary<Utils.ProfileName, byte[]> Inventories = new();
	private static readonly Dictionary<ZNetPeer, Utils.ProfileName> peerProfileNameMap = new();

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
	private static class PatchZNetOnNewConnection
	{
		private static byte[] deriveKey(long time)
		{
			Rfc2898DeriveBytes encryptionKey = new(ServerCharacters.serverKey.Value, BitConverter.GetBytes(time), 1000);
			return encryptionKey.GetBytes(32);
		}

		[UsedImplicitly]
		private static void Postfix(ZNet __instance, ZNetPeer peer)
		{
			if (__instance.IsServer())
			{
				if (ServerCharacters.maintenanceMode.GetToggle() && !isAdmin(peer.m_rpc))
				{
					peer.m_rpc.Invoke("Error", ServerCharacters.MaintenanceDisconnectMagic);
					Utils.Log($"Non-admin client {Utils.GetPlayerID(peer.m_rpc.GetSocket().GetHostName())} tried to connect during maintenance and got disconnected");
					__instance.Disconnect(peer);
				}

				peer.m_rpc.Register("ServerCharacters PlayerProfile", Shared.receiveCompressedFromPeer((peerRpc, profileData) => onReceivedProfile(peerRpc, profileData)));
				peer.m_rpc.Register("ServerCharacters CheckSignature", Shared.receiveCompressedFromPeer(onReceivedSignature));
				peer.m_rpc.Register("ServerCharacters PlayerInventory", Shared.receiveCompressedFromPeer(onReceivedInventory));
				peer.m_rpc.Register("ServerCharacters PlayerDied", Shared.receiveCompressedFromPeer(onPlayerDied));
				peer.m_rpc.Register<string, string, string>("ServerCharacters ResetSkill", onResetSkill);
				peer.m_rpc.Register<string, int, string, string>("ServerCharacters RaiseSkill", onRaiseSkill);
				peer.m_rpc.Register<string, string>("ServerCharacters GetPlayerPos", onGetPlayerPos);
				peer.m_rpc.Register<string, int, string, string>("ServerCharacters GiveItem", onGiveItem);
				peer.m_rpc.Register<string, string, Vector3>("ServerCharacters SendOwnPos", onSendOwnPos);

				long time = DateTime.Now.Ticks;
				byte[] key = deriveKey(time);

				ZPackage package = new();
				package.Write(key);
				package.Write(time);
				peer.m_rpc.Invoke("ServerCharacters KeyExchange", package);
			}
		}

		private static void onPlayerDied(ZRpc peerRpc, byte[] profileData)
		{
			PlayerProfile? profile = onReceivedProfile(peerRpc, profileData);
			if (profile is not null)
			{
				backupProfile(profile);
				string profilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + profile.m_filename + ".fch";
				File.Delete(profilePath + ".old");
				File.Move(profilePath, profilePath + ".old");
			}
		}

		private static PlayerProfile? onReceivedProfile(ZRpc peerRpc, byte[] profileData)
		{
			PlayerProfile profile = new(fileSource: FileHelpers.FileSource.Local);
			if (!profile.LoadPlayerProfileFromBytes(profileData))
			{
				Utils.Log($"Encountered invalid data for bytes from steam ID {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())}");
				// invalid data ...
				return null;
			}

			if (Shared.CharacterNameIsForbidden(profile.GetName()))
			{
				peerRpc.Invoke("Error", ServerCharacters.CharacterNameDisconnectMagic);
				ZNet.instance.Disconnect(ZNet.instance.GetPeer(peerRpc));
				Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} tried to connect with a bad profile name '{profile.GetName()}' and got disconnected");
				return null;
			}

			profile.m_filename = Utils.GetPlayerID(peerRpc.m_socket.GetHostName()) + "_" + profile.GetName().ToLower();
			profile.SavePlayerToDisk();
			Utils.Log($"Saved player profile data for {profile.m_filename}");

			return profile;
		}

		private static void onReceivedInventory(ZRpc peerRpc, byte[] inventoryData) => Inventories[Utils.ProfileName.fromPeer(ZNet.instance.GetPeer(peerRpc))] = inventoryData;

		private static void onReceivedSignature(ZRpc peerRpc, byte[] signedProfile)
		{
			ZPackage signedProfilePackage = new(signedProfile);
			byte[] profileData = signedProfilePackage.ReadByteArray();
			byte[] signature = signedProfilePackage.ReadByteArray();

			long profileSavedTicks = VerifySignature(profileData, signature);
			if (profileSavedTicks <= 0)
			{
				Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} tried to restore an emergency backup, but signature was invalid. Skipping.");
				return;
			}

			PlayerProfile profile = new(fileSource: FileHelpers.FileSource.Local);
			if (!profile.LoadPlayerProfileFromBytes(profileData) || Shared.CharacterNameIsForbidden(profile.GetName()))
			{
				// invalid data ...
				Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} tried to restore an emergency backup, but the profile data is corrupted.");
				return;
			}

			profile.m_filename = Utils.GetPlayerID(peerRpc.m_socket.GetHostName()) + "_" + profile.GetName().ToLower();

			string profilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + profile.m_filename + ".fch";
			FileInfo profileFileInfo = new(profilePath);
			if (!profileFileInfo.Exists)
			{
				Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} tried to restore an emergency backup for the character '{profile.m_filename}' that does not belong to this server. Skipping.");
				return;
			}

			DateTime lastModification = profileFileInfo.LastWriteTime;
			DateTime profileSavedTime = new(profileSavedTicks);
			if (profileSavedTime <= lastModification)
			{
				// profile too old
				Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} tried to restore an old emergency backup. Skipping.");
				return;
			}

			if (!Inventories.TryGetValue(Utils.ProfileName.fromPeer(ZNet.instance.GetPeer(peerRpc)), out byte[] inventory))
			{
				PlayerProfile oldProfile = new(profile.m_filename);
				oldProfile.LoadPlayerFromDisk();
				inventory = ReadInventoryFromProfile(oldProfile);
			}

			PatchPlayerProfileInventory(profile, inventory);

			profile.SavePlayerToDisk();
			Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} succesfully restored an emergency backup for {profile.m_filename}.");
		}

		private static long VerifySignature(byte[] profileData, byte[] signature)
		{
			byte[] profileHash = SHA512.Create().ComputeHash(profileData);

			ZPackage package = new(signature);
			byte[] encryptedHash = package.ReadByteArray();
			byte[] iv = package.ReadByteArray();
			long time = package.ReadLong();
			byte[] key = deriveKey(time);

			Aes aes = Aes.Create();
			aes.Key = key;
			aes.IV = iv;
			MemoryStream inputStream = new(encryptedHash);
			CryptoStream cryptoStream = new(inputStream, aes.CreateDecryptor(), CryptoStreamMode.Read);
			MemoryStream outputStream = new();
			cryptoStream.CopyTo(outputStream);
			byte[] decryptedHash = outputStream.ToArray();

			return profileHash.SequenceEqual(decryptedHash) ? time : 0;
		}
	}

	private static bool isAdmin(ZRpc? rpc)
	{
		return rpc is null || ZNet.instance.ListContainsId(ZNet.instance.m_adminList, rpc.GetSocket().GetHostName());
	}

	public static void onGiveItem(ZRpc? peerRpc, string itemName, int itemQuantity, string targetPlayerName, string targetPlayerId)
	{
		if (!isAdmin(peerRpc))
		{
			return;
		}

		List<ZNetPeer> onlinePlayers = ZNet.m_instance.m_peers;
		foreach (ZNetPeer player in onlinePlayers)
		{
			if (string.Compare(targetPlayerName, player.m_playerName, StringComparison.InvariantCultureIgnoreCase) == 0 && (targetPlayerId == "0" || Utils.GetPlayerID(player.m_socket.GetHostName()) == Utils.GetPlayerID(targetPlayerId)))
			{
				player.m_rpc.Invoke("ServerCharacters GiveItem", itemName, itemQuantity);
				return;
			}
		}
	}

	private static void onGetPlayerPos(ZRpc peerRpc, string targetPlayerName, string targetPlayerId)
	{
		if (!isAdmin(peerRpc))
		{
			return;
		}

		List<ZNetPeer> onlinePlayers = ZNet.m_instance.m_peers;
		foreach (ZNetPeer player in onlinePlayers)
		{
			if (string.Compare(targetPlayerName, player.m_playerName, StringComparison.InvariantCultureIgnoreCase) == 0 && (targetPlayerId == "0" || Utils.GetPlayerID(player.m_socket.GetHostName()) == Utils.GetPlayerID(targetPlayerId)))
			{
				peerRpc.Invoke("ServerCharacters GetPlayerPos", player.GetRefPos());
				return;
			}
		}

		peerRpc.Invoke("ServerCharacters GetPlayerPos", Vector3.zero);
	}

	private static void onSendOwnPos(ZRpc peerRpc, string targetPlayerName, string targetPlayerId, Vector3 pos)
	{
		if (!isAdmin(peerRpc))
		{
			return;
		}

		List<ZNetPeer> onlinePlayers = ZNet.m_instance.m_peers;
		foreach (ZNetPeer player in onlinePlayers)
		{
			if (string.Compare(targetPlayerName, player.m_playerName, StringComparison.InvariantCultureIgnoreCase) == 0 && (targetPlayerId == "0" || Utils.GetPlayerID(player.m_socket.GetHostName()) == Utils.GetPlayerID(targetPlayerId)))
			{
				player.m_rpc.Invoke("ServerCharacters TeleportTo", pos);
				peerRpc.Invoke("ServerCharacters SendOwnPos", player.GetRefPos());
				return;
			}
		}

		peerRpc.Invoke("ServerCharacters SendOwnPos", Vector3.zero);
	}

	public static void onResetSkill(ZRpc? peerRpc, string skillName, string targetPlayerName, string targetPlayerId)
	{
		if (!isAdmin(peerRpc))
		{
			return;
		}

		List<ZNetPeer> onlinePlayers = ZNet.m_instance.m_peers;
		foreach (ZNetPeer player in onlinePlayers)
		{
			if (targetPlayerName == "" || string.Compare(targetPlayerName, player.m_playerName, StringComparison.InvariantCultureIgnoreCase) == 0 && (targetPlayerId == "0" || Utils.GetPlayerID(player.m_socket.GetHostName()) == Utils.GetPlayerID(targetPlayerId)))
			{
				player.m_rpc.Invoke("ServerCharacters ResetSkill", skillName);
			}
		}

		foreach (string s in Directory.GetFiles(Utils.CharacterSavePath))
		{
			FileInfo file = new(s);

			try
			{
				if (Utils.IsServerCharactersFilePattern(file.Name))
				{
					string[] parts = file.Name.Split('_');
					string Id = $"{parts[0]}_{parts[1]}";
					string Name = parts[2].Split('.')[0];
					if ((targetPlayerId == "0" || Utils.GetPlayerID(targetPlayerId) == Id) && (targetPlayerName == "" || string.Compare(targetPlayerName, Name, StringComparison.InvariantCultureIgnoreCase) == 0))
					{
						PlayerProfile profile = new($"{file.Name.Replace(".fch", "")}", FileHelpers.FileSource.Local);
						profile.LoadPlayerFromDisk();

						Skills skills = ReadSkillsFromProfile(profile);

						skills.CheatResetSkill(skillName);

						ZPackage pkg = new();
						skills.Save(pkg);
						PatchPlayerProfileSkills(profile, pkg.GetArray());
						profile.SavePlayerToDisk();
					}
				}
			}
			catch (Exception e)
			{
				Utils.Log($"Removing skill failed for profile {file.Name.Replace(".fch", "")}: {e}");
			}
		}
	}

	public static void onRaiseSkill(ZRpc? peerRpc, string skill, int level, string targetPlayerName, string targetPlayerId)
	{
		if (!isAdmin(peerRpc))
		{
			return;
		}

		List<ZNetPeer> onlinePlayers = ZNet.m_instance.m_peers;
		foreach (ZNetPeer player in onlinePlayers)
		{
			if (targetPlayerName == "" || string.Compare(targetPlayerName, player.m_playerName, StringComparison.InvariantCultureIgnoreCase) == 0 && (targetPlayerId == "0" || Utils.GetPlayerID(player.m_socket.GetHostName()) == Utils.GetPlayerID(targetPlayerId)))
			{
				player.m_rpc.Invoke("ServerCharacters RaiseSkill", skill, level);
			}
		}

		foreach (string s in Directory.GetFiles(Utils.CharacterSavePath))
		{
			FileInfo file = new(s);

			try
			{
				if (Utils.IsServerCharactersFilePattern(file.Name))
				{
					string[] parts = file.Name.Split('_');
					string Id = $"{parts[0]}_{parts[1]}";
					string Name = parts[2].Split('.')[0];
					if ((targetPlayerId == "0" || Utils.GetPlayerID(targetPlayerId) == Id) && (targetPlayerName == "" || string.Compare(targetPlayerName, Name, StringComparison.InvariantCultureIgnoreCase) == 0))
					{
						PlayerProfile profile = new($"{file.Name.Replace(".fch", "")}", FileHelpers.FileSource.Local);
						profile.LoadPlayerFromDisk();

						Skills skills = ReadSkillsFromProfile(profile);

						skills.CheatRaiseSkill(skill, level);

						ZPackage pkg = new();
						skills.Save(pkg);
						PatchPlayerProfileSkills(profile, pkg.GetArray());
						profile.SavePlayerToDisk();
					}
				}
			}
			catch (Exception e)
			{
				Utils.Log($"Raise skill failed for profile {file.Name.Replace(".fch", "")}: {e}");
			}
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
	private class PatchZNetDisconnect
	{
		private static void Prefix(ZNetPeer peer)
		{
			if (ZNet.instance?.IsServer() == true && peerProfileNameMap.TryGetValue(peer, out Utils.ProfileName profileName) && Inventories.TryGetValue(profileName, out byte[] inventoryData))
			{
				Inventories.Remove(profileName);

				PlayerProfile playerProfile = new(profileName.id + "_" + profileName.name.ToLower(), FileHelpers.FileSource.Local);
				if (playerProfile.LoadPlayerFromDisk())
				{
					FileInfo profileFile = new(Utils.CharacterSavePath + Path.DirectorySeparatorChar + playerProfile.GetFilename() + ".fch");
					DateTime originalWriteTime = profileFile.LastWriteTime;
					PatchPlayerProfileInventory(playerProfile, inventoryData);
					playerProfile.SavePlayerToDisk();
					File.SetLastWriteTime(profileFile.FullName, originalWriteTime);
				}
			}

			peerProfileNameMap.Remove(peer);
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
	private class SendConfigsAfterLogin
	{
		private class BufferingSocket : ISocket
		{
			public volatile bool finished = false;
			public volatile int versionMatchQueued = -1;
			public readonly List<ZPackage> Package = new();
			public readonly ISocket Original;

			public BufferingSocket(ISocket original)
			{
				Original = original;
			}

			public bool IsConnected() => Original.IsConnected();
			public ZPackage Recv() => Original.Recv();
			public int GetSendQueueSize() => Original.GetSendQueueSize();
			public int GetCurrentSendRate() => Original.GetCurrentSendRate();
			public bool IsHost() => Original.IsHost();
			public void Dispose() => Original.Dispose();
			public bool GotNewData() => Original.GotNewData();
			public void Close() => Original.Close();
			public string GetEndPointString() => Original.GetEndPointString();
			public void GetAndResetStats(out int totalSent, out int totalRecv) => Original.GetAndResetStats(out totalSent, out totalRecv);
			public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec) => Original.GetConnectionQuality(out localQuality, out remoteQuality, out ping, out outByteSec, out inByteSec);
			public ISocket Accept() => Original.Accept();
			public int GetHostPort() => Original.GetHostPort();
			public bool Flush() => Original.Flush();
			public string GetHostName() => Original.GetHostName();

			public void VersionMatch()
			{
				if (finished)
				{
					Original.VersionMatch();
				}
				else
				{
					versionMatchQueued = Package.Count;
				}
			}

			public void Send(ZPackage pkg)
			{
				int oldPos = pkg.GetPos();
				pkg.SetPos(0);
				int methodHash = pkg.ReadInt();
				if ((methodHash == "PeerInfo".GetStableHashCode() || methodHash == "RoutedRPC".GetStableHashCode() || methodHash == "ZDOData".GetStableHashCode()) && !finished)
				{
					ZPackage newPkg = new(pkg.GetArray());
					newPkg.SetPos(oldPos);
					Package.Add(newPkg); // the original ZPackage gets reused, create a new one
				}
				else
				{
					pkg.SetPos(oldPos);
					Original.Send(pkg);
				}
			}
		}

		[HarmonyPriority(Priority.First)]
		private static void Prefix(ref BufferingSocket? __state, ZNet __instance, ZRpc rpc)
		{
			if (__instance.IsServer())
			{
				__state = new BufferingSocket(rpc.GetSocket());
				rpc.m_socket = __state;
				if (ZNet.instance.GetPeer(rpc) is { } peer && ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
				{
					peer.m_socket = __state;
				}
			}
		}

		private static void Postfix(BufferingSocket __state, ZNet __instance, ZRpc rpc)
		{
			if (!__instance.IsServer())
			{
				return;
			}

			ZNetPeer peer = __instance.GetPeer(rpc);

			peerProfileNameMap[peer] = Utils.ProfileName.fromPeer(peer);

			IEnumerator sendAsync()
			{
				if (peer.m_uid != 0)
				{
					PlayerProfile playerProfile = new(Utils.GetPlayerID(peer.m_socket.GetHostName()) + "_" + peer.m_playerName.ToLower(), FileHelpers.FileSource.Local);
					byte[] playerProfileData = playerProfile.LoadPlayerDataFromDisk()?.GetArray() ?? Array.Empty<byte>();

					if (playerProfileData.Length == 0 && ServerCharacters.singleCharacterMode.GetToggle() && !__instance.ListContainsId(__instance.m_adminList, peer.m_rpc.GetSocket().GetHostName()) && Utils.GetPlayerListFromFiles().playerLists.Any(p => p.Id == Utils.GetPlayerID(peer.m_rpc.GetSocket().GetHostName())))
					{
						peer.m_rpc.Invoke("Error", ServerCharacters.SingleCharacterModeDisconnectMagic);
						Utils.Log($"Non-admin client {Utils.GetPlayerID(peer.m_rpc.GetSocket().GetHostName())} tried to create a second character and got disconnected");
						__instance.Disconnect(peer);
						yield break;
					}

					if (!ServerCharacters.backupOnlyMode.GetToggle())
					{
						foreach (bool sending in Shared.sendCompressedDataToPeer(peer, "ServerCharacters PlayerProfile", playerProfileData))
						{
							if (!sending)
							{
								yield return null;
							}
						}
					}
				}

				if (rpc.GetSocket() is BufferingSocket bufferingSocket)
				{
					rpc.m_socket = bufferingSocket.Original;
					peer.m_socket = bufferingSocket.Original;
				}

				bufferingSocket = __state;
				bufferingSocket.finished = true;

				for (int i = 0; i < bufferingSocket.Package.Count; ++i)
				{
					if (i == bufferingSocket.versionMatchQueued)
					{
						bufferingSocket.Original.VersionMatch();
					}
					bufferingSocket.Original.Send(bufferingSocket.Package[i]);
				}
				if (bufferingSocket.Package.Count == bufferingSocket.versionMatchQueued)
				{
					bufferingSocket.Original.VersionMatch();
				}
			}

			__instance.StartCoroutine(sendAsync());
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.InternalKick), typeof(ZNetPeer))]
	private static class PatchZNetKick
	{
		private static readonly MethodInfo DisconnectSender = AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.SendDisconnect), new[] { typeof(ZNetPeer) });

		[UsedImplicitly]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> instructionList = instructions.ToList();
			foreach (CodeInstruction instruction in instructionList)
			{
				yield return instruction;
				if (instruction.opcode == OpCodes.Call && instruction.OperandIs(DisconnectSender))
				{
					yield return instructionList.Last(); // ret opcode
					// Skip this.Disconnect() call
					yield break;
				}
			}
		}

		[UsedImplicitly]
		private static void Postfix(ZNet __instance, ZNetPeer peer)
		{
			if (!peer.m_socket.IsConnected())
			{
				__instance.Disconnect(peer);
				return;
			}

			int endTime = ServerCharacters.monotonicCounter + 30;

			IEnumerator shutdownAfterSave()
			{
				yield return new WaitWhile(() => peer.m_socket.IsConnected() && endTime > ServerCharacters.monotonicCounter);
				__instance.Disconnect(peer);
			}

			__instance.StartCoroutine(shutdownAfterSave());
		}
	}

	[HarmonyPatch(typeof(Version), nameof(Version.GetVersionString))]
	private static class PatchVersionGetVersionString
	{
		[HarmonyPriority(Priority.Last)]
		private static void Postfix(ref string __result)
		{
			if (ZNet.instance?.IsServer() == true && (ZNet.instance.m_hostSocket != null || !ZNet.m_openServer))
			{
				__result += "-ServerCharacters";
			}
		}
	}

	[HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerToDisk))]
	private static class PatchPlayerProfileSave_Server
	{
		private static void Postfix(PlayerProfile __instance)
		{
			if (ZNet.instance?.IsServer() == true)
			{
				backupProfile(__instance);
			}
		}
	}

	private static void backupProfile(PlayerProfile profile)
	{
		string saveFile = PlayerProfile.GetCharacterFolderPath(profile.m_fileSource) + profile.m_filename + ".fch.old";
		if (FileHelpers.Exists(saveFile, profile.m_fileSource))
		{
			Directory.CreateDirectory(Utils.CharacterSavePath + Path.DirectorySeparatorChar + "backups");
			using FileStream zipToOpen = new(Utils.CharacterSavePath + Path.DirectorySeparatorChar + "backups" + Path.DirectorySeparatorChar + profile.m_filename + ".zip", FileMode.OpenOrCreate);
			using ZipArchive archive = new(zipToOpen, ZipArchiveMode.Update);

			while (archive.Entries.Count >= ServerCharacters.backupsToKeep.Value)
			{
				archive.Entries.First().Delete();
			}

			string fileName = profile.m_filename + "-" + DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss") + ".fch";
			using Stream archiveFileStream = archive.CreateEntry(fileName).Open();
			FileReader reader = new(saveFile, profile.m_fileSource);
			(reader.m_stream?.BaseStream ?? reader.m_binary.BaseStream).CopyTo(archiveFileStream);
			reader.Dispose();
			Utils.Log($"Backed up a player profile in '{fileName}'");
		}

		string[] parts = profile.m_filename.Split('_');
		Utils.Cache.profiles[new Utils.ProfileName { id = parts.Length > 1 ? $"{parts[0]}_{parts[1]}" : parts[0], name = profile.GetName() }] = profile;
	}

	public static void generateServerKey()
	{
		if (ServerCharacters.serverKey.Value == "")
		{
			byte[] key = new byte[32];
			using RNGCryptoServiceProvider rngCsp = new();
			rngCsp.GetBytes(key);

			ServerCharacters.serverKey.Value = Convert.ToBase64String(key);
		}
	}

	private static byte[] ReadInventoryFromProfile(PlayerProfile profile)
	{
		ZPackage playerPackage = new(profile.m_playerData);
		ConsumePlayerSaveUntilInventory(playerPackage);
		int startPos = playerPackage.GetPos();
		new Inventory("Inventory", null, 8, 4).Load(playerPackage);
		int endPos = playerPackage.GetPos();

		return profile.m_playerData.Skip(startPos).Take(endPos - startPos).ToArray();
	}

	private static void PatchPlayerProfileInventory(PlayerProfile profile, byte[] inventoryData)
	{
		ZPackage playerPackage = new(profile.m_playerData);
		ConsumePlayerSaveUntilInventory(playerPackage);
		int startPos = playerPackage.GetPos();
		new Inventory("Inventory", null, 8, 4).Load(playerPackage);
		int endPos = playerPackage.GetPos();

		byte[] newData = new byte[startPos + (profile.m_playerData.LongLength - endPos) + inventoryData.Length];
		Array.Copy(profile.m_playerData, newData, startPos);
		Array.Copy(inventoryData, 0, newData, startPos, inventoryData.Length);
		Array.Copy(profile.m_playerData, endPos, newData, startPos + inventoryData.Length, profile.m_playerData.LongLength - endPos);

		profile.m_playerData = newData;
	}

	class DummyPlayer : Player
	{
		public override void Message(MessageHud.MessageType type, string msg, int amount = 0, Sprite? icon = null)
		{
		}
	}

	private static Skills ReadSkillsFromProfile(PlayerProfile profile)
	{
		Skills skills = new()
		{
			m_skills = ((Player)Resources.FindObjectsOfTypeAll(typeof(Player))[0]).GetComponent<Skills>().m_skills,
			// ReSharper disable once Unity.IncorrectMonoBehaviourInstantiation
			m_player = new DummyPlayer()
		};
		ZPackage playerPackage = new(profile.m_playerData);
		ConsumePlayerSaveUntilSkills(playerPackage);
		skills.Load(playerPackage);

		return skills;
	}

	private static void PatchPlayerProfileSkills(PlayerProfile profile, byte[] skillData)
	{
		ZPackage playerPackage = new(profile.m_playerData);
		ConsumePlayerSaveUntilSkills(playerPackage);
		int startPos = playerPackage.GetPos();
		new GameObject().AddComponent<Skills>().Load(playerPackage);
		int endPos = playerPackage.GetPos();

		byte[] newData = new byte[startPos + (profile.m_playerData.LongLength - endPos) + skillData.Length];
		Array.Copy(profile.m_playerData, newData, startPos);
		Array.Copy(skillData, 0, newData, startPos, skillData.Length);
		Array.Copy(profile.m_playerData, endPos, newData, startPos + skillData.Length, profile.m_playerData.LongLength - endPos);

		profile.m_playerData = newData;
	}

	public static void ConsumePlayerSaveUntilInventory(ZPackage pkg)
	{
		throw new NotImplementedException("Was not patched ...");
	}

	public static void ConsumePlayerSaveUntilSkills(ZPackage pkg)
	{
		throw new NotImplementedException("Was not patched ...");
	}

	// This transpiler removes all manipulation on the Player object, leaving only the bare calls to Read*() functions on the ZPackage, up to the Inventory.Load() or Skills.Load call. All arguments of operations on Player objects are popped away and the return value replaced by a dummy value.
	// We can use this to observe how far Player.Load() reads into the ZPackage before reading the inventory, allowing us to splice it in and out from raw profile player data.
	private static IEnumerable<CodeInstruction> PlayerProfileConsumeUntil(ILGenerator ilGenerator, MethodInfo endOperand)
	{
		List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.Load)), ilGenerator).ToList();
		for (int i = 0; i < instructions.Count; ++i)
		{
			if (instructions[i].opcode == OpCodes.Ldarg_0)
			{
				if (instructions[i + 1].opcode == OpCodes.Ldfld)
				{
					if (instructions[i + 1].operand is FieldInfo field && field.FieldType.IsClass)
					{
						if (field.FieldType == typeof(Inventory))
						{
							yield return new CodeInstruction(OpCodes.Ldnull)
							{
								labels = instructions[i++].labels
							};
							yield return new CodeInstruction(OpCodes.Ldnull);
							yield return new CodeInstruction(OpCodes.Ldc_I4_0);
							yield return new CodeInstruction(OpCodes.Ldc_I4_0);
							yield return new CodeInstruction(OpCodes.Newobj, typeof(Inventory).GetConstructors()[0]);
						}
						else
						{
							yield return new CodeInstruction(OpCodes.Ldtoken, field.FieldType)
							{
								labels = instructions[i++].labels
							};
							yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Type), nameof(Type.GetTypeFromHandle)));
							yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AccessTools), nameof(AccessTools.CreateInstance), new[] { typeof(Type) }));
						}
					}
					else
					{
						yield return new CodeInstruction(OpCodes.Ldc_I4_0)
						{
							labels = instructions[i++].labels
						};
					}

					continue;
				}
			}

			if (instructions[i].opcode == OpCodes.Stfld)
			{
				yield return new CodeInstruction(OpCodes.Pop);
				yield return new CodeInstruction(OpCodes.Pop);
				continue;
			}

			if (instructions[i].opcode == OpCodes.Ldarg_1)
			{
				instructions[i].opcode = OpCodes.Ldarg_0;
				yield return instructions[i];
				continue;
			}

			if (instructions[i].opcode == OpCodes.Callvirt && instructions[i].OperandIs(endOperand))
			{
				yield return new CodeInstruction(OpCodes.Pop);
				yield return new CodeInstruction(OpCodes.Pop);
				yield return new CodeInstruction(OpCodes.Ret) { labels = instructions[i + 1].labels };
				break;
			}

			if ((instructions[i].opcode == OpCodes.Callvirt || instructions[i].opcode == OpCodes.Call) && instructions[i].operand is MethodInfo method && (method.DeclaringType?.IsAssignableFrom(typeof(Player)) == true || method.DeclaringType?.IsAssignableFrom(typeof(ZLog)) == true) && method.Name != "op_Equality")
			{
				for (int j = method.IsStatic ? 0 : -1; j < method.GetParameters().Length; ++j)
				{
					yield return new CodeInstruction(OpCodes.Pop);
				}

				if (method.ReturnType != typeof(void))
				{
					if (method.ReturnType == typeof(float))
					{
						yield return new CodeInstruction(OpCodes.Ldc_R4, 0f);
					}
					else if (method.ReturnType == typeof(int))
					{
						yield return new CodeInstruction(OpCodes.Ldc_I4_0);
					}
					else
					{
						yield return new CodeInstruction(OpCodes.Ldnull);
					}
				}

				continue;
			}

			yield return instructions[i];
		}
	}

	[HarmonyPatch(typeof(ServerSide), nameof(ConsumePlayerSaveUntilInventory))]
	private static class PlayerProfileConsumptionStartInventory
	{
		[UsedImplicitly]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilGenerator)
		{
			MethodInfo inventorySave = AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.Load));
			return PlayerProfileConsumeUntil(ilGenerator, inventorySave);
		}
	}

	[HarmonyPatch(typeof(ServerSide), nameof(ConsumePlayerSaveUntilSkills))]
	private static class PlayerProfileConsumptionStartSkills
	{
		[UsedImplicitly]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilGenerator)
		{
			MethodInfo skillsSave = AccessTools.DeclaredMethod(typeof(Skills), nameof(Skills.Load));
			return PlayerProfileConsumeUntil(ilGenerator, skillsSave);
		}
	}
}

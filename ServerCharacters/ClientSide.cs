using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using YamlDotNet.Serialization;

namespace ServerCharacters
{
	public static class ClientSide
	{
		public static bool serverCharacter = false;
		private static bool currentlySaving = false;
		private static bool forceSynchronousSaving = false;
		private static bool acquireCharacterFromTemplate = false;
		private static bool doEmergencyBackup = false;

		private static PlayerSnapshot? playerSnapShotLast;
		private static PlayerSnapshot? playerSnapShotNew;

		private static byte[]? serverEncryptionKey;
		private static long serverEncryptionTime;

		private static string? connectionError;

		[HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerData))]
		private static class PatchPlayerProfilePlayerSave
		{
			[UsedImplicitly]
			private static void Prefix()
			{
				if (serverCharacter && doEmergencyBackup && playerSnapShotLast != null && Player.m_localPlayer is Player player)
				{
					player.m_inventory.m_inventory = playerSnapShotLast.inventory;
					player.m_knownStations = playerSnapShotLast.knownStations;
					player.m_knownTexts = playerSnapShotLast.knownTexts;
				}
			}
		}

		[HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerToDisk))]
		private static class PatchPlayerProfileSave_Client
		{
			private static byte[] SaveCharacterToServer(byte[] packageArray, PlayerProfile profile)
			{
				if (serverCharacter && doEmergencyBackup && serverEncryptionKey != null)
				{
					File.WriteAllBytes(global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + profile.m_filename + ".fch.signature", generateProfileSignature(packageArray, serverEncryptionKey));
					File.WriteAllBytes(global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + profile.m_filename + ".fch.serverbackup", packageArray);
					doEmergencyBackup = false;
				}

				if (!serverCharacter || currentlySaving || ZNet.instance?.GetServerPeer()?.IsReady() != true)
				{
					return packageArray;
				}

				IEnumerator saveAsync()
				{
					foreach (bool sending in Shared.sendCompressedDataToPeer(ZNet.instance.GetServerPeer(), "ServerCharacters PlayerProfile", packageArray))
					{
						if (!sending)
						{
							yield return null;
						}
					}

					currentlySaving = false;
				}
				if (forceSynchronousSaving)
				{
					foreach (bool sending in Shared.sendCompressedDataToPeer(ZNet.instance.GetServerPeer(), "ServerCharacters PlayerProfile", packageArray))
					{
						if (!sending)
						{
							Thread.Sleep(10); // busy loop, force waiting before continuing...
						}
					}
				}
				else
				{
					currentlySaving = true;
					ZNet.instance.StartCoroutine(saveAsync());
				}

				return packageArray;
			}

			private static readonly MethodInfo ArrayWriter = AccessTools.DeclaredMethod(typeof(BinaryWriter), nameof(BinaryWriter.Write), new[] { typeof(byte[]) });
			private static readonly MethodInfo ServerCharacterSaver = AccessTools.DeclaredMethod(typeof(PatchPlayerProfileSave_Client), nameof(SaveCharacterToServer));

			[UsedImplicitly]
			private static IEnumerable<CodeInstruction> Transpiler(MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				return new CodeMatcher(instructions)
					.MatchForward(false, new CodeMatch(new CodeInstruction(OpCodes.Callvirt, ArrayWriter))) // it writes the array first, then the hash
					.Insert(new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, ServerCharacterSaver))
					.Instructions();
			}
		}

		[HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
		private static class PatchGameShutdown
		{
			private static readonly MethodInfo getZNetInstance = AccessTools.DeclaredPropertyGetter(typeof(ZNet), nameof(ZNet.instance));

			[UsedImplicitly]
			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				foreach (CodeInstruction instruction in instructions)
				{
					if (instruction.opcode == OpCodes.Call && instruction.OperandIs(getZNetInstance))
					{
						yield return new CodeInstruction(OpCodes.Ret);
						yield break;
					}
					yield return instruction;
				}
			}

			[UsedImplicitly]
			private static void Postfix()
			{
				static void Shutdown()
				{
					serverCharacter = false;
					ZNet.ConnectionStatus originalConnectionStatus = ZNet.m_connectionStatus;
					ZNet.instance.Shutdown();
					if (originalConnectionStatus > ZNet.ConnectionStatus.Connected)
					{
						// avoid resetting connection status during shutdown
						ZNet.m_connectionStatus = originalConnectionStatus;
					}
				}

				if (currentlySaving)
				{
					static IEnumerator shutdownAfterSave()
					{
						yield return new WaitWhile(() => currentlySaving);
						Shutdown();
					}

					ZNet.instance.StartCoroutine(shutdownAfterSave());
				}
				else
				{
					Shutdown();
				}
			}
		}

		[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
		private static class PatchZNetOnNewConnection
		{
			[UsedImplicitly]
			private static void Postfix(ZNet __instance, ZNetPeer peer)
			{
				if (!ZNet.instance.IsServer())
				{
					peer.m_rpc.Register("ServerCharacters PlayerProfile", Shared.receiveCompressedFromPeer(onReceivedProfile));
					peer.m_rpc.Register<ZPackage>("ServerCharacters KeyExchange", receiveEncryptionKeyFromServer);
					peer.m_rpc.Register<string>("ServerCharacters IngameMessage", onReceivedIngameMessage);
					peer.m_rpc.Register<string>("ServerCharacters KickMessage", onReceivedKickMessage);

					string signatureFilePath = global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.signature";
					string backupFilePath = global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.serverbackup";

					if (File.Exists(signatureFilePath) && File.Exists(backupFilePath))
					{
						Utils.Log($"Found emergency backup and signature for character '{Game.instance.m_playerProfile.m_filename}'. Trying to restore the backup.");

						ZPackage package = new();
						package.Write(File.ReadAllBytes(backupFilePath));
						package.Write(File.ReadAllBytes(signatureFilePath));
						foreach (bool sending in Shared.sendCompressedDataToPeer(peer, "ServerCharacters CheckSignature", package.GetArray()))
						{
							if (!sending)
							{
								Thread.Sleep(10); // busy loop, force waiting before continuing...
							}
						}
					}
				}
			}

			private static void onReceivedIngameMessage(ZRpc peerRpc, string message)
			{
				Chat.instance.AddString(message);
				Chat.instance.m_hideTimer = 0f;
				MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, message);
			}
			
			private static void onReceivedKickMessage(ZRpc peerRpc, string message)
			{
				connectionError = message;
			}

			private static void onReceivedProfile(ZRpc peerRpc, byte[] profileData)
			{
				PlayerProfile profile = new();
				if (profileData.Length == 0)
				{
					if (Game.instance.m_playerProfile.m_worldData.Count != 0)
					{
						Game.instance.Logout();
						ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
						connectionError = "Please create a new character, before connecting to this server, to avoid loss of data.";
					}
					else
					{
						serverCharacter = true;
						acquireCharacterFromTemplate = true;
					}

					return;
				}

				if (!profile.LoadPlayerProfileFromBytes(profileData) || Shared.CharacterNameIsForbidden(profile.m_playerName))
				{
					Game.instance.Logout();
					ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
					connectionError = "The saved data on the server was corrupt, please contact your server admin or create a new character.";
					return;
				}

				serverCharacter = true;

				profile.m_filename = Game.instance.m_playerProfile.m_filename;
				Game.instance.m_playerProfile = profile;

				string signatureFilePath = global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.signature";
				string backupFilePath = global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.serverbackup";

				if (File.Exists(backupFilePath) && File.Exists(signatureFilePath))
				{
					File.Delete(signatureFilePath);
					File.Delete(backupFilePath);
					Utils.Log($"Deleted emergency backup from {backupFilePath}");
				}
			}
		}

		[HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
		private class InitializePlayerFromTemplate
		{
			[UsedImplicitly]
			private static void Postfix()
			{
				if (!acquireCharacterFromTemplate)
				{
					return;
				}
				acquireCharacterFromTemplate = false;

				try
				{
					PlayerTemplate? template = new DeserializerBuilder().IgnoreFields().Build().Deserialize<PlayerTemplate?>(ServerCharacters.playerTemplate.Value);
					if (template == null)
					{
						return;
					}

					foreach (Skills.SkillDef skill in Player.m_localPlayer.GetSkills().m_skills)
					{
						if (template.skills.TryGetValue(skill.m_skill.ToString(), out float skillValue))
						{
							Player.m_localPlayer.GetSkills().GetSkill(skill.m_skill).m_level = skillValue;
						}
					}

					Inventory inventory = Player.m_localPlayer.m_inventory;
					foreach (KeyValuePair<string, int> item in template.items)
					{
						inventory.AddItem(item.Key, item.Value, 1, 0, 0, "");
					}

					if (template.spawn is PlayerTemplate.Position spawnPos)
					{
						Player.m_localPlayer.transform.position = new Vector3(spawnPos.x, spawnPos.y, spawnPos.z);
					}
				}
				catch (SerializationException)
				{
				}
			}
		}

		[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.ShowConnectError))]
		private class ShowConnectionError
		{
			private static void Postfix(FejdStartup __instance)
			{
				if ((int)ZNet.GetConnectionStatus() == ServerCharacters.MaintenanceDisconnectMagic)
				{
					__instance.m_connectionFailedError.text = "Server is undergoing maintenance. Please try again later.";
				}
				if ((int)ZNet.GetConnectionStatus() == ServerCharacters.CharacterNameDisconnectMagic)
				{
					__instance.m_connectionFailedError.text = "Your character name contains illegal characters. Please choose a different name.";
				}
				if ((int)ZNet.GetConnectionStatus() == ServerCharacters.SingleCharacterModeDisconnectMagic)
				{
					__instance.m_connectionFailedError.text = "You are not allowed to create more than one character on this server.";
				}
				if (__instance.m_connectionFailedPanel.activeSelf && connectionError != null)
				{
					__instance.m_connectionFailedError.text += "\n" + connectionError;
					connectionError = null;
				}
			}
		}

		[HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_Disconnect))]
		private class PatchZNetRPC_Disconnect
		{
			[UsedImplicitly]
			private static void Prefix(ZNet __instance)
			{
				if (__instance.IsServer())
				{
					return;
				}

				if (serverCharacter)
				{
					forceSynchronousSaving = true;
					Game.instance.SavePlayerProfile(true);
					forceSynchronousSaving = false;
				}
			}
		}

		[HarmonyPatch(typeof(Game), nameof(Game.OnApplicationQuit))]
		private class PatchGameOnApplicationQuit
		{
			[UsedImplicitly]
			private static void Prefix()
			{
				forceSynchronousSaving = true;
			}
		}

		private static byte[] generateProfileSignature(byte[] profileData, byte[] key)
		{
			byte[] profileHash = SHA512.Create().ComputeHash(profileData);

			Aes aes = Aes.Create();
			aes.Key = key;
			MemoryStream outputStream = new();
			CryptoStream cryptoStream = new(outputStream, aes.CreateEncryptor(), CryptoStreamMode.Write);
			cryptoStream.Write(profileHash, 0, profileHash.Length);
			cryptoStream.FlushFinalBlock();
			cryptoStream.Close();

			ZPackage package = new();
			package.Write(outputStream.ToArray());
			package.Write(aes.IV);
			package.Write(serverEncryptionTime);

			return package.GetArray();
		}

		private static void receiveEncryptionKeyFromServer(ZRpc peerRpc, ZPackage keyPackage)
		{
			serverEncryptionKey = keyPackage.ReadByteArray();
			serverEncryptionTime = keyPackage.ReadLong();
		}

		[HarmonyPatch(typeof(Game), nameof(Game.Logout))]
		private class PatchGameLogout
		{
			[UsedImplicitly]
			private static void Prefix()
			{
				doEmergencyBackup = ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connecting && ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected && !Game.instance.IsShuttingDown();
				if (doEmergencyBackup)
				{
					Utils.Log("Lost connection to the server. Preparing for emergency backup of profile data.");
				}
			}
		}

		private class PlayerSnapshot
		{
			public List<ItemDrop.ItemData> inventory = null!;
			public Dictionary<string, int> knownStations = null!;
			public Dictionary<string, string> knownTexts = null!;
		}

		public static void snapShotProfile()
		{
			if (ZNet.instance?.IsServer() == false && Player.m_localPlayer != null)
			{
				if (ZNet.instance.GetServerPing() < 1.5f)
				{
					playerSnapShotLast = playerSnapShotNew;
				}

				playerSnapShotNew = new PlayerSnapshot
				{
					inventory = Player.m_localPlayer.GetInventory().m_inventory.Select(d => d.Clone()).ToList(),
					knownStations = Player.m_localPlayer.m_knownStations.ToDictionary(t => t.Key, t => t.Value),
					knownTexts = Player.m_localPlayer.m_knownTexts.ToDictionary(t => t.Key, t => t.Value)
				};
			}
		}

		[HarmonyPatch(typeof(Inventory), nameof(Inventory.Changed))]
		private class PatchInventoryChanged
		{
			private static void Prefix(Inventory __instance)
			{
				if (__instance == Player.m_localPlayer?.m_inventory && ZNet.instance.GetServerPeer() is ZNetPeer serverPeer)
				{
					ZPackage inventoryPackage = new();
					__instance.Save(inventoryPackage);
					IEnumerator saveAsync()
					{
						foreach (bool sending in Shared.sendCompressedDataToPeer(serverPeer, "ServerCharacters PlayerInventory", inventoryPackage.GetArray()))
						{
							if (!sending)
							{
								yield return null;
							}
						}
					}
					ZNet.instance.StartCoroutine(saveAsync());
				}
			}
		}
	}
}

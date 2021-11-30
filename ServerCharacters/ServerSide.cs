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

namespace ServerCharacters
{
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
					if (ServerCharacters.maintenanceMode.GetToggle() && !__instance.m_adminList.Contains(peer.m_rpc.GetSocket().GetHostName()))
					{
						peer.m_rpc.Invoke("Error", ServerCharacters.MaintenanceDisconnectMagic);
						Utils.Log($"Non-admin client {peer.m_rpc.GetSocket().GetHostName()} tried to connect during maintenance and got disconnected");
						__instance.Disconnect(peer);
					}
					
					peer.m_rpc.Register("ServerCharacters PlayerProfile", Shared.receiveCompressedFromPeer(onReceivedProfile));
					peer.m_rpc.Register("ServerCharacters CheckSignature", Shared.receiveCompressedFromPeer(onReceivedSignature));
					peer.m_rpc.Register("ServerCharacters PlayerInventory", Shared.receiveCompressedFromPeer(onReceivedInventory));

					long time = DateTime.Now.Ticks;
					byte[] key = deriveKey(time);

					ZPackage package = new();
					package.Write(key);
					package.Write(time);
					peer.m_rpc.Invoke("ServerCharacters KeyExchange", package);
				}
			}

			private static void onReceivedProfile(ZRpc peerRpc, byte[] profileData)
			{
				PlayerProfile profile = new();
				if (!profile.LoadPlayerProfileFromBytes(profileData))
				{
					// invalid data ...
					return;
				}
				if (Shared.CharacterNameIsForbidden(profile.GetName()))
				{
					peerRpc.Invoke("Error", ServerCharacters.CharacterNameDisconnectMagic);
					ZNet.instance.Disconnect(ZNet.instance.GetPeer(peerRpc));
					Utils.Log($"Client {peerRpc.GetSocket().GetHostName()} tried to connect with a bad profile name '{profile.GetName()}' and got disconnected");
					return;
				}
				profile.m_filename = peerRpc.GetSocket().GetHostName() + "_" + profile.GetName();
				profile.SavePlayerToDisk();
				Utils.Log($"Saved player profile data for {profile.m_filename}");
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
					Utils.Log($"Client {peerRpc.GetSocket().GetHostName()} tried to restore an emergency backup, but signature was invalid. Skipping.");
					return;
				}

				PlayerProfile profile = new();
				if (!profile.LoadPlayerProfileFromBytes(profileData) || Shared.CharacterNameIsForbidden(profile.GetName()))
				{
					// invalid data ...
					Utils.Log($"Client {peerRpc.GetSocket().GetHostName()} tried to restore an emergency backup, but the profile data is corrupted.");
					return;
				}
				profile.m_filename = peerRpc.GetSocket().GetHostName() + "_" + profile.GetName();

				string profilePath = global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + profile.m_filename + ".fch";
				FileInfo profileFileInfo = new(profilePath);
				if (!profileFileInfo.Exists)
				{
					Utils.Log($"Client {peerRpc.GetSocket().GetHostName()} tried to restore an emergency backup for the character '{profile.m_filename}' that does not belong to this server. Skipping.");
					return;
				}

				DateTime lastModification = profileFileInfo.LastWriteTime;
				DateTime profileSavedTime = new(profileSavedTicks);
				if (profileSavedTime <= lastModification)
				{
					// profile too old
					Utils.Log($"Client {peerRpc.GetSocket().GetHostName()} tried to restore an old emergency backup. Skipping.");
					return;
				}

				if (!Inventories.TryGetValue(Utils.ProfileName.fromPeer(ZNet.instance.GetPeer(peerRpc)), out byte[] inventory))
				{
					PlayerProfile oldProfile = new(profile.m_filename);
					oldProfile.LoadPlayerFromDisk();
					inventory = ReadInventoryFromProfile(oldProfile);
				}
				PatchPlayerProfile(profile, inventory);

				profile.SavePlayerToDisk();
				Utils.Log($"Client {peerRpc.GetSocket().GetHostName()} succesfully restored an emergency backup for {profile.m_filename}.");
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

		[HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
		private class PatchZNetDisconnect
		{
			private static void Prefix(ZNetPeer peer)
			{
				if (ZNet.instance?.IsServer() == true && peerProfileNameMap.TryGetValue(peer, out Utils.ProfileName profileName) && Inventories.TryGetValue(profileName, out byte[] inventoryData))
				{
					Inventories.Remove(profileName);
					
					PlayerProfile playerProfile = new(profileName.id + "_" + profileName.name);
					if (playerProfile.LoadPlayerFromDisk())
					{
						FileInfo profileFile = new(global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + playerProfile.GetFilename() + ".fch");
						DateTime originalWriteTime = profileFile.LastWriteTime;
						PatchPlayerProfile(playerProfile, inventoryData);
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

				public void Send(ZPackage pkg)
				{
					pkg.SetPos(0);
					int methodHash = pkg.ReadInt();
					if ((methodHash == "PeerInfo".GetStableHashCode() || methodHash == "RoutedRPC".GetStableHashCode()) && !finished)
					{
						Package.Add(new ZPackage(pkg.GetArray())); // the original ZPackage gets reused, create a new one
					}
					else
					{
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
					AccessTools.DeclaredField(typeof(ZRpc), nameof(ZRpc.m_socket)).SetValue(rpc, __state);
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
					PlayerProfile playerProfile = new(peer.m_socket.GetHostName() + "_" + peer.m_playerName);
					byte[] playerProfileData = playerProfile.LoadPlayerDataFromDisk()?.GetArray() ?? Array.Empty<byte>();
					
					if (playerProfileData.Length == 0 && ServerCharacters.singleCharacterMode.GetToggle() && !__instance.m_adminList.Contains(peer.m_rpc.GetSocket().GetHostName()) && Utils.GetPlayerListFromFiles().playerLists.Any(p => p.Id == peer.m_rpc.GetSocket().GetHostName()))
					{
						peer.m_rpc.Invoke("Error", ServerCharacters.SingleCharacterModeDisconnectMagic);
						Utils.Log($"Non-admin client {peer.m_rpc.GetSocket().GetHostName()} tried to create a second character and got disconnected");
						__instance.Disconnect(peer);
						yield break;
					}

					foreach (bool sending in Shared.sendCompressedDataToPeer(peer, "ServerCharacters PlayerProfile", playerProfileData))
					{
						if (!sending)
						{
							yield return null;
						}
					}

					if (rpc.GetSocket() is BufferingSocket bufferingSocket)
					{
						rpc.m_socket = bufferingSocket.Original;
					}

					bufferingSocket = __state;
					bufferingSocket.finished = true;

					foreach (ZPackage package in bufferingSocket.Package)
					{
						bufferingSocket.Original.Send(package);
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
				if (ZNet.instance?.IsServer() == true)
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
					string saveFile = global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + __instance.m_filename + ".fch.old";
					if (File.Exists(saveFile))
					{
						Directory.CreateDirectory(global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + "backups");
						using FileStream zipToOpen = new(global::Utils.GetSaveDataPath() + Path.DirectorySeparatorChar + "characters" + Path.DirectorySeparatorChar + "backups" + Path.DirectorySeparatorChar + __instance.m_filename + ".zip", FileMode.OpenOrCreate);
						using ZipArchive archive = new(zipToOpen, ZipArchiveMode.Update);

						while (archive.Entries.Count >= ServerCharacters.backupsToKeep.Value)
						{
							archive.Entries.First().Delete();
						}

						string fileName = __instance.m_filename + "-" + DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss") + ".fch";
						archive.CreateEntryFromFile(saveFile, fileName);
						Utils.Log($"Backed up a player profile in '{fileName}'");
					}
					
					Utils.Cache.profiles[new Utils.ProfileName { id = __instance.m_filename.Split('_')[0], name = __instance.GetName() }] = __instance;
				}
			}
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

		private static void PatchPlayerProfile(PlayerProfile profile, byte[] inventoryData)
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
		
		public static void ConsumePlayerSaveUntilInventory(ZPackage pkg)
		{
			throw new NotImplementedException("Was not patched ...");
		}

		// This transpiler removes all manipulation on the Player object, leaving only the bare calls to Read*() functions on the ZPackage, up to the Inventory.Load() call. All arguments of operations on Player objects are popped away and the return value replaced by a dummy value.
		// We can use this to observe how far Player.Load() reads into the ZPackage before reading the inventory, allowing us to splice it in and out from raw profile player data.
		[HarmonyPatch(typeof(ServerSide), nameof(ConsumePlayerSaveUntilInventory))]
		private static class PlayerProfileConsumptionStart
		{
			[UsedImplicitly]
			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> _instructions, ILGenerator ilGenerator)
			{
				MethodInfo inventorySave = AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.Load));
				List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.Load)), ilGenerator).ToList();
				for (int i = 0; i < instructions.Count; ++i)
				{
					if (instructions[i].opcode == OpCodes.Ldarg_0)
					{
						if (instructions[i + 1].opcode == OpCodes.Ldfld)
						{
							yield return new CodeInstruction(OpCodes.Ldc_I4_0)
							{
								labels = instructions[i++].labels
							};
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
					if (instructions[i].opcode == OpCodes.Callvirt && instructions[i].OperandIs(inventorySave))
					{
						yield return new CodeInstruction(OpCodes.Pop);
						yield return new CodeInstruction(OpCodes.Pop);
						yield return new CodeInstruction(OpCodes.Ret);
						break;
					}
					if ((instructions[i].opcode == OpCodes.Callvirt || instructions[i].opcode == OpCodes.Call) && instructions[i].operand is MethodInfo method && method.DeclaringType?.IsAssignableFrom(typeof(Player)) == true)
					{
						for (int j = -1; j < method.GetParameters().Length; ++j)
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
		}
	}
}

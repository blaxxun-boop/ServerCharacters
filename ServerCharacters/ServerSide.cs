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

				string profilePath = global::Utils.GetSaveDataPath() + "/characters/" + profile.m_filename + ".fch";
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
					AccessTools.DeclaredField(typeof(ZRpc), "m_socket").SetValue(rpc, __state);
				}
			}

			private static void Postfix(BufferingSocket __state, ZNet __instance, ZRpc rpc)
			{
				if (!__instance.IsServer())
				{
					return;
				}

				ZNetPeer peer = (ZNetPeer)AccessTools.DeclaredMethod(typeof(ZNet), "GetPeer", new[] { typeof(ZRpc) }).Invoke(__instance, new object[] { rpc });

				IEnumerator sendAsync()
				{
					PlayerProfile playerProfile = new(peer.m_socket.GetHostName() + "_" + peer.m_playerName);
					byte[] playerProfileData = playerProfile.LoadPlayerDataFromDisk()?.GetArray() ?? new byte[0];

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
					string saveFile = global::Utils.GetSaveDataPath() + "/characters/" + __instance.m_filename + ".fch.old";
					if (File.Exists(saveFile))
					{
						Directory.CreateDirectory(global::Utils.GetSaveDataPath() + "/characters/backups");
						using FileStream zipToOpen = new(global::Utils.GetSaveDataPath() + "/characters/backups/" + __instance.m_filename + ".zip", FileMode.OpenOrCreate);
						using ZipArchive archive = new(zipToOpen, ZipArchiveMode.Update);

						while (archive.Entries.Count >= ServerCharacters.backupsToKeep.Value)
						{
							archive.Entries.First().Delete();
						}

						string fileName = __instance.m_filename + "-" + DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss") + ".fch";
						archive.CreateEntryFromFile(saveFile, fileName);
						Utils.Log($"Backed up a player profile in '{fileName}'");
					}
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
	}
}

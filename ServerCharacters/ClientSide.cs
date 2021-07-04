using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace ServerCharacters
{
	public static class ClientSide
	{
		private static bool serverCharacter = false;
		private static bool currentlySaving = false;
		private static bool forceSynchronousSaving = false;

		private static string? connectionError;

		[HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerToDisk))]
		private static class PatchPlayerProfileSave_Client
		{
			private static byte[] SaveCharacterToServer(byte[] packageArray)
			{
				if (!serverCharacter || currentlySaving || ZNet.instance?.GetServerPeer()?.IsReady() != true)
				{
					return packageArray;
				}

				IEnumerator saveAsync()
				{
					foreach (bool sending in Shared.sendProfileToPeer(ZNet.instance.GetServerPeer(), packageArray))
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
					foreach (bool sending in Shared.sendProfileToPeer(ZNet.instance.GetServerPeer(), packageArray))
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
					.Insert(new CodeInstruction(OpCodes.Call, ServerCharacterSaver))
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
					peer.m_rpc.Register("ServerCharacters PlayerProfile", Shared.receiveProfileFromPeer(onReceivedProfile));
				}
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
	}
}

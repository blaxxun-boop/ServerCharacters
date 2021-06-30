using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace ServerCharacters
{
	public static class ClientSide
	{
		private static bool serverCharacter = false;
		private static bool currentlySaving = false;
		
		private static string? connectionError;

		[HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerToDisk))]
		private static class PatchPlayerProfileSave
		{
			private static byte[] SaveCharacterToServer(byte[] packageArray)
			{
				if (!serverCharacter || currentlySaving)
				{
					return packageArray;
				}

				currentlySaving = true;
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
				ZNet.instance.StartCoroutine(saveAsync());

				return packageArray;
			}

			private static readonly MethodInfo ArrayWriter = AccessTools.DeclaredMethod(typeof(BinaryWriter), nameof(BinaryWriter.Write), new[] { typeof(byte[]) });
			private static readonly MethodInfo ServerCharacterSaver = AccessTools.DeclaredMethod(typeof(PatchPlayerProfileSave), nameof(SaveCharacterToServer));

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

				if (!profile.LoadPlayerProfileFromBytes(profileData))
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
				if (__instance.m_connectionFailedPanel.activeSelf && connectionError != null)
				{
					__instance.m_connectionFailedError.text += "\n" + connectionError;
					connectionError = null;
				}
			}
		}
	}
}

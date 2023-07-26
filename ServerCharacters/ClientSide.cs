using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using Steamworks;
using UnityEngine;
using YamlDotNet.Serialization;
using Random = UnityEngine.Random;

namespace ServerCharacters;

public static class ClientSide
{
	public static bool serverCharacter = false;
	private static bool currentlySaving = false;
	private static bool forceSynchronousSaving = false;
	private static bool acquireCharacterFromTemplate = false;
	private static bool doEmergencyBackup = false;
	private static bool iDied = false;

	private static PlayerSnapshot? playerSnapShotLast;
	private static PlayerSnapshot? playerSnapShotNew;

	private static byte[]? serverEncryptionKey;
	private static long serverEncryptionTime;

	private static string? connectionError;

	[HarmonyPatch(typeof(Skills), nameof(Skills.ResetSkill))]
	public class SaveLastSkillReset
	{
		public static Skills.SkillType last = Skills.SkillType.All;

		[UsedImplicitly]
		public static void Finalizer(Skills.SkillType skillType) => last = skillType;
	}

	[HarmonyPatch(typeof(Console), nameof(Console.Print))]
	public class SilenceConsole
	{
		public static bool silence = false;

		[UsedImplicitly]
		public static bool Prefix() => !silence;
	}

	private static TaskCompletionSource<Vector3>? awaitingPos = null;

	[HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
	public class AddChatCommands
	{
		private static void Postfix()
		{
			_ = new Terminal.ConsoleCommand("ServerCharacters", "Manages the ServerCharacters commands.", (Terminal.ConsoleEvent)(args =>
			{
				if (!ServerCharacters.configSync.IsAdmin)
				{
					args.Context.AddString("You are not an admin on this server.");
					return;
				}

				Skills.SkillType GetSkillType(string name)
				{
					Dictionary<Skills.SkillType, Skills.Skill> skillData = Player.m_localPlayer.GetSkills().m_skillData;
					Dictionary<Skills.SkillType, Skills.Skill> backup = skillData.ToDictionary(kv => kv.Key, kv => new Skills.Skill(kv.Value.m_info) { m_accumulator = kv.Value.m_accumulator, m_level = kv.Value.m_level });

					SaveLastSkillReset.last = Skills.SkillType.All;
					try
					{
						SilenceConsole.silence = true;
						Player.m_localPlayer.GetSkills().CheatResetSkill(name);
					}
					finally
					{
						SilenceConsole.silence = false;
					}

					Utils.OverwriteDict(backup, skillData);

					return SaveLastSkillReset.last;
				}

				if (args.Length >= 3 && args[1] == "resetskill")
				{
					Skills.SkillType skill = GetSkillType(args[2]);
					if (skill == Skills.SkillType.All)
					{
						args.Context.AddString($"{args[2]} is not a valid skill.");
						return;
					}

					if (args.Length > 3)
					{
						string name = args[3];
						int lastArg = 4;
						if (name.StartsWith("\""))
						{
							name = name.Substring(1);
							while (lastArg < args.Length && !name.EndsWith("\"", StringComparison.Ordinal))
							{
								name += " " + args[lastArg++];
							}
							name = name.Substring(0, name.Length - 1);
						}

						ZNet.instance.GetServerPeer().m_rpc.Invoke("ServerCharacters ResetSkill", args[2], name, args.Length > lastArg ? args[lastArg] : "0");
						args.Context.AddString($"{args[2]} has been reset for {name}.");
					}
					else
					{
						ZNet.instance.GetServerPeer().m_rpc.Invoke("ServerCharacters ResetSkill", args[2], "", "0");
						args.Context.AddString($"{args[2]} has been reset for everyone.");
					}

					return;
				}

				if (args.Length >= 3 && args[1] == "raiseskill")
				{
					Skills.SkillType skill = GetSkillType(args[2]);
					if (skill == Skills.SkillType.All)
					{
						args.Context.AddString($"{args[2]} is not a valid skill.");
						return;
					}

					if (args.Length > 4)
					{
						string name = args[4];
						int lastArg = 5;
						if (name.StartsWith("\""))
						{
							name = name.Substring(1);
							while (lastArg < args.Length && !name.EndsWith("\"", StringComparison.Ordinal))
							{
								name += " " + args[lastArg++];
							}
							name = name.Substring(0, name.Length - 1);
						}

						ZNet.instance.GetServerPeer().m_rpc.Invoke("ServerCharacters RaiseSkill", args[2], int.Parse(args[3]), name, args.Length > lastArg ? args[lastArg] : "0");
						args.Context.AddString($"{args[2]} has been raised by {args[3]} for {name}.");
					}
					else
					{
						ZNet.instance.GetServerPeer().m_rpc.Invoke("ServerCharacters RaiseSkill", args[2], int.Parse(args[3]), "", "0");
						args.Context.AddString($"{args[2]} has been raised by {args[3]} for everyone.");
					}

					return;
				}

				if (args.Length >= 3 && args[1] == "giveitem")
				{
					GameObject item = ObjectDB.instance.GetItemPrefab(args[2]);
					if (item is null)
					{
						args.Context.AddString($"{args[2]} is not a valid item.");
						return;
					}

					if (args.Length > 4)
					{
						string name = args[4];
						int lastArg = 5;
						if (name.StartsWith("\""))
						{
							name = name.Substring(1);
							while (lastArg < args.Length && !name.EndsWith("\"", StringComparison.Ordinal))
							{
								name += " " + args[lastArg++];
							}
							name = name.Substring(0, name.Length - 1);
						}

						ZNet.instance.GetServerPeer().m_rpc.Invoke("ServerCharacters GiveItem", args[2], int.Parse(args[3]), name, args.Length > lastArg ? args[lastArg] : "0");
						args.Context.AddString($"{args[3]}x {args[2]} has been given to {name}, if their inventory isn't full.");
					}
					else
					{
						args.Context.AddString($"Please specify a target player.");
					}

					return;
				}

				if (args.Length >= 3 && args[1] == "teleport")
				{
					string name = args[2];
					int lastArg = 3;
					if (name.StartsWith("\""))
					{
						name = name.Substring(1);
						while (lastArg < args.Length && !name.EndsWith("\"", StringComparison.Ordinal))
						{
							name += " " + args[lastArg++];
						}
						name = name.Substring(0, name.Length - 1);
					}

					ZNet.instance.GetServerPeer().m_rpc.Invoke("ServerCharacters GetPlayerPos", name, args.Length > lastArg ? args[lastArg] : "0");

					IEnumerator AwaitResponse()
					{
						awaitingPos = new TaskCompletionSource<Vector3>();
						Task<Vector3> task = awaitingPos.Task;
						yield return new WaitUntil(() => task.IsCompleted);

						Vector3 pos = task.Result;
						if (pos == Vector3.zero)
						{
							args.Context.AddString("A player with this name is not online.");
						}
						else
						{
							Player.m_localPlayer.TeleportTo(pos, Quaternion.identity, true);
						}
					}
					ServerCharacters.selfReference.StartCoroutine(AwaitResponse());
					return;
				}

				if (args.Length >= 3 && args[1] == "summon")
				{
					string name = args[2];
					int lastArg = 3;
					if (name.StartsWith("\""))
					{
						name = name.Substring(1);
						while (lastArg < args.Length && !name.EndsWith("\"", StringComparison.Ordinal))
						{
							name += " " + args[lastArg++];
						}
						name = name.Substring(0, name.Length - 1);
					}

					ZNet.instance.GetServerPeer().m_rpc.Invoke("ServerCharacters SendOwnPos", name, args.Length > lastArg ? args[lastArg] : "0", Player.m_localPlayer.transform.position);

					IEnumerator AwaitResponse()
					{
						awaitingPos = new TaskCompletionSource<Vector3>();
						Task<Vector3> task = awaitingPos.Task;
						yield return new WaitUntil(() => task.IsCompleted);

						Vector3 pos = task.Result;
						args.Context.AddString(pos == Vector3.zero ? "A player with this name is not online." : "The player is being summoned, please wait a second.");
					}
					ServerCharacters.selfReference.StartCoroutine(AwaitResponse());
					return;
				}

				args.Context.AddString("ServerCharacters console commands - use 'ServerCharacters' followed by one of the following options.");
				args.Context.AddString("resetskill [skillname] [playername] [id] - resets the skill for the specified player. Steam / Xbox ID is optional and only required, if multiple players have the same name. If no name is provided, the skill is reset for every character on the server, online and offline.");
				args.Context.AddString("raiseskill [skillname] [level] [playername] [id] - raises the skill for the specified player by the specified level. Steam / Xbox ID is optional and only required, if multiple players have the same name. If no name is provided, the skill is raised for every character on the server, online and offline.");
				args.Context.AddString("teleport [playername] [steamid] - teleports you to the specified player. Quote names with a space. Steam / Xbox ID is optional and only required, if multiple players have the same name.");
				args.Context.AddString("summon [playername] [steamid] - teleports the specified player to you. Quote names with a space. Steam / Xbox ID is optional and only required, if multiple players have the same name.");
				args.Context.AddString("giveitem [itemname] [quantity] [playername] [id] - adds the specified item to the specified players inventory in the specified quantity. Quote names with a space. Steam / Xbox ID is optional and only required, if multiple players have the same name. Will fail, if their inventory is full.");
			}), optionsFetcher: () => new List<string> { "resetskill", "teleport", "summon", "raiseskill", "giveitem" });
		}
	}

	[HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerData))]
	private static class PatchPlayerProfilePlayerSave
	{
		[UsedImplicitly]
		private static void Prefix()
		{
			if (serverCharacter && doEmergencyBackup && playerSnapShotLast != null && Player.m_localPlayer is { } player)
			{
				player.m_inventory.m_inventory = playerSnapShotLast.inventory;
				Utils.OverwriteDict(playerSnapShotLast.knownStations, player.m_knownStations);
				Utils.OverwriteDict(playerSnapShotLast.knownTexts, player.m_knownTexts);
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
				File.WriteAllBytes(Utils.CharacterSavePath + Path.DirectorySeparatorChar + profile.m_filename + ".fch.signature", generateProfileSignature(packageArray, serverEncryptionKey));
				File.WriteAllBytes(Utils.CharacterSavePath + Path.DirectorySeparatorChar + profile.m_filename + ".fch.serverbackup", packageArray);
				doEmergencyBackup = false;
			}

			if (!serverCharacter || currentlySaving || ZNet.instance?.GetServerPeer()?.IsReady() != true)
			{
				return packageArray;
			}

			IEnumerator saveAsync()
			{
				foreach (bool sending in Shared.sendCompressedDataToPeer(ZNet.instance.GetServerPeer(), iDied && ServerCharacters.hardcoreMode.GetToggle() ? "ServerCharacters PlayerDied" : "ServerCharacters PlayerProfile", packageArray))
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
				foreach (bool sending in Shared.sendCompressedDataToPeer(ZNet.instance.GetServerPeer(), iDied && ServerCharacters.hardcoreMode.GetToggle() ? "ServerCharacters PlayerDied" : "ServerCharacters PlayerProfile", packageArray))
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

	[HarmonyPatch]
	private class EnableSocketLinger
	{
		private static void dummy() { }

		private static MethodInfo TargetMethod() => Type.GetType(nameof(ZSteamSocket) + ", assembly_valheim") is { } steamSocket ? AccessTools.DeclaredMethod(steamSocket, nameof(ZSteamSocket.Close)) : AccessTools.DeclaredMethod(typeof(EnableSocketLinger), nameof(dummy));

		private static MethodInfo socketClose => AccessTools.Method(typeof(SteamNetworkingSockets), nameof(SteamNetworkingSockets.CloseConnection));

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codeInstructions)
		{
			foreach (CodeInstruction instruction in codeInstructions)
			{
				if (instruction.opcode == OpCodes.Call && instruction.OperandIs(socketClose))
				{
					yield return new CodeInstruction(OpCodes.Pop);
					yield return new CodeInstruction(OpCodes.Ldc_I4_1);
				}
				yield return instruction;
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
	private static class PatchGameShutdown
	{
		[UsedImplicitly]
		private static void Prefix()
		{
			forceSynchronousSaving = true;
		}

		[UsedImplicitly]
		private static void Finalizer()
		{
			serverCharacter = false;
			forceSynchronousSaving = false;
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Save))]
	private static class StorePoisonDebuff
	{
		private static void Prefix(Player __instance)
		{
			if (ServerCharacters.storePoison.GetToggle())
			{
				if (__instance.m_seman.GetStatusEffect("Poison".GetStableHashCode()) is SE_Poison poison && !__instance.IsDead())
				{
					__instance.m_customData["ServerCharacters PoisonDamage"] = poison.m_damageLeft.ToString(CultureInfo.InvariantCulture);
					__instance.m_customData["ServerCharacters PoisonDamageHit"] = poison.m_damagePerHit.ToString(CultureInfo.InvariantCulture);
					__instance.m_customData["ServerCharacters PoisonTTL"] = poison.m_ttl.ToString(CultureInfo.InvariantCulture);
				}
				else
				{
					__instance.m_customData.Remove("ServerCharacters PoisonDamage");
					__instance.m_customData.Remove("ServerCharacters PoisonDamageHit");
					__instance.m_customData.Remove("ServerCharacters PoisonTTL");
				}
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Load))]
	private static class LoadPoisonDebuff
	{
		private static void Postfix(Player __instance)
		{
			if (ServerCharacters.storePoison.GetToggle() && __instance.m_customData.TryGetValue("ServerCharacters PoisonDamage", out string poisonString) && poisonString != "")
			{
				SE_Poison poison = (SE_Poison)__instance.m_seman.AddStatusEffect("Poison".GetStableHashCode());
				poison.m_damageLeft = float.Parse(__instance.m_customData["ServerCharacters PoisonDamage"], CultureInfo.InvariantCulture);
				poison.m_damagePerHit = float.Parse(__instance.m_customData["ServerCharacters PoisonDamageHit"], CultureInfo.InvariantCulture);
				poison.m_ttl = float.Parse(__instance.m_customData["ServerCharacters PoisonTTL"], CultureInfo.InvariantCulture);
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
				peer.m_rpc.Register<string>("ServerCharacters ResetSkill", onReceivedResetSkill);
				peer.m_rpc.Register<string, int>("ServerCharacters RaiseSkill", onReceivedRaiseSkill);
				peer.m_rpc.Register<Vector3>("ServerCharacters GetPlayerPos", onReceivedPlayerPos);
				peer.m_rpc.Register<string, int>("ServerCharacters GiveItem", onReceivedGiveItem);
				peer.m_rpc.Register<Vector3>("ServerCharacters SendOwnPos", onReceivedOwnPos);
				peer.m_rpc.Register<Vector3>("ServerCharacters TeleportTo", onReceivedTeleportTo);

				string signatureFilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.signature";
				string backupFilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.serverbackup";

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

		private static void onReceivedOwnPos(ZRpc peerRpc, Vector3 pos)
		{
			if (awaitingPos is not null)
			{
				awaitingPos.SetResult(pos);
				awaitingPos = null;
			}
		}

		private static void onReceivedTeleportTo(ZRpc peerRpc, Vector3 pos)
		{
			Player.m_localPlayer.TeleportTo(pos, Quaternion.identity, true);
		}

		private static void onReceivedPlayerPos(ZRpc peerRpc, Vector3 pos)
		{
			if (awaitingPos is not null)
			{
				awaitingPos.SetResult(pos);
				awaitingPos = null;
			}
		}

		private static void onReceivedGiveItem(ZRpc peerRpc, string itemName, int itemQuantity)
		{
			string message = Player.m_localPlayer.m_inventory.AddItem(ObjectDB.instance.GetItemPrefab(itemName), itemQuantity) ? $"An admin added {itemQuantity}x {itemName} to your inventory." : $"An admin tried to add {itemQuantity}x {itemName} to your inventory, but it is full.";

			Chat.instance.AddString(message);
			Chat.instance.m_hideTimer = 0f;
			MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, message);
		}

		private static void onReceivedResetSkill(ZRpc peerRpc, string skill)
		{
			Player.m_localPlayer.m_skills.CheatResetSkill(skill);

			string message = $"An admin reset your skill {skill}";
			Chat.instance.AddString(message);
			Chat.instance.m_hideTimer = 0f;
			MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, message);
		}

		private static void onReceivedRaiseSkill(ZRpc peerRpc, string skill, int level)
		{
			Player.m_localPlayer.m_skills.CheatRaiseSkill(skill, level);

			string message = $"An admin raised your skill {skill} by {level}";
			Chat.instance.AddString(message);
			Chat.instance.m_hideTimer = 0f;
			MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, message);
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

			PlayerProfile profile = new(Game.instance.m_playerProfile.m_filename);
			if (!profile.LoadPlayerProfileFromBytes(profileData) || Shared.CharacterNameIsForbidden(profile.m_playerName))
			{
				Game.instance.Logout();
				ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
				connectionError = "The saved data on the server was corrupt, please contact your server admin or create a new character.";
				return;
			}

			serverCharacter = true;
			Game.instance.m_playerProfile = profile;

			string signatureFilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.signature";
			string backupFilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.serverbackup";

			if (File.Exists(backupFilePath) && File.Exists(signatureFilePath))
			{
				File.Delete(signatureFilePath);
				File.Delete(backupFilePath);
				Utils.Log($"Deleted emergency backup from {backupFilePath}");
			}
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
	private static class DetectBackupOnlyMode
	{
		[UsedImplicitly]
		private static void Postfix(ZNet __instance)
		{
			if (ServerCharacters.backupOnlyMode.GetToggle() && !__instance.IsServer())
			{
				serverCharacter = true;
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
	private class InitializePlayerFromTemplate
	{
		[UsedImplicitly]
		private static void Postfix()
		{
			if (ServerCharacters.backupOnlyMode.GetToggle() ? Game.instance.GetPlayerProfile().HaveLogoutPoint() || Game.instance.GetPlayerProfile().HaveCustomSpawnPoint() : !acquireCharacterFromTemplate)
			{
				return;
			}
			acquireCharacterFromTemplate = false;

			Player.m_localPlayer.m_inventory.RemoveAll();
			Player.m_localPlayer.m_skills.m_skillData.Clear();
			Player.m_localPlayer.m_knownMaterial.Clear();
			Player.m_localPlayer.m_knownRecipes.Clear();
			Player.m_localPlayer.m_knownStations.Clear();
			Player.m_localPlayer.m_knownTexts.Clear();
			Player.m_localPlayer.m_uniques.Clear();
			Player.m_localPlayer.m_trophies.Clear();
			Player.m_localPlayer.m_customData.Clear();
			Player.m_localPlayer.GiveDefaultItems();

			try
			{
				PlayerTemplate? template = new DeserializerBuilder().IgnoreFields().Build().Deserialize<PlayerTemplate?>(ServerCharacters.playerTemplate.Value);
				if (template != null)
				{
					foreach (KeyValuePair<string, float> skillKv in template.skills)
					{
						Player.m_localPlayer.GetSkills().CheatRaiseSkill(skillKv.Key, skillKv.Value);
					}

					Inventory inventory = Player.m_localPlayer.m_inventory;
					foreach (KeyValuePair<string, int> item in template.items)
					{
						inventory.AddItem(item.Key, item.Value, 1, 0, 0, "");
					}

					if (template.spawn is { Count: > 0 } spawnPos)
					{
						Random.State oldState = Random.state;
						Random.InitState(UserInfo.GetLocalUser().NetworkUserId.GetStableHashCode());
						int index = Random.Range(0, spawnPos.Count - 1);
						Random.state = oldState;
						Player.m_localPlayer.transform.position = new Vector3(spawnPos[index].x, spawnPos[index].y, spawnPos[index].z);
					}
				}
			}
			catch (SerializationException)
			{
			}

			Game.instance.SavePlayerProfile(true);

			if (ServerCharacters.firstLoginMessage.Value != "")
			{
				foreach (Player p in Player.GetAllPlayers())
				{
					p.Message(MessageHud.MessageType.Center, ServerCharacters.firstLoginMessage.Value.Replace("{name}", Player.m_localPlayer.GetHoverName()));
				}
			}
		}
	}

	[HarmonyPatch(typeof(Valkyrie), nameof(Valkyrie.Awake))]
	private class ChangeValkyrieTarget
	{
		private static void Prefix(Valkyrie __instance)
		{
			if (!__instance.GetComponent<ZNetView>().IsOwner())
			{
				return;
			}

			try
			{
				PlayerTemplate? template = new DeserializerBuilder().IgnoreFields().Build().Deserialize<PlayerTemplate?>(ServerCharacters.playerTemplate.Value);
				if (template == null)
				{
					return;
				}

				if (template.spawn is { Count: > 0 } spawnPos)
				{
					Random.State oldState = Random.state;
					Random.InitState(UserInfo.GetLocalUser().NetworkUserId.GetStableHashCode());
					int index = Random.Range(0, spawnPos.Count - 1);
					Random.state = oldState;
					Player.m_localPlayer.transform.position = new Vector3(spawnPos[index].x, spawnPos[index].y, spawnPos[index].z);
				}
			}
			catch (SerializationException)
			{
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Awake))]
	private static class DisableValkyrieAndIntro
	{
		private static void Postfix(Player __instance)
		{
			if (ServerCharacters.newCharacterIntro.Value == Intro.Disabled)
			{
				__instance.m_valkyrie = null;
			}
		}
	}

	[HarmonyPatch(typeof(Valkyrie), nameof(Valkyrie.ShowText))]
	private static class DisableIntro
	{
		private static bool Prefix(Valkyrie __instance)
		{
			if (ServerCharacters.newCharacterIntro.Value == Intro.Valkyrie)
			{
				__instance.m_startPause = 0;
				return false;
			}

			return true;
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.FindSpawnPoint))]
	private class ReplaceSpawnPoint
	{
		private static bool CheckCustomSpawnPoint(out Vector3 pos)
		{
			try
			{
				PlayerTemplate? template = new DeserializerBuilder().IgnoreFields().Build().Deserialize<PlayerTemplate?>(ServerCharacters.playerTemplate.Value);
				if (template == null)
				{
					pos = Vector3.zero;
					return false;
				}

				if (template.spawn is { Count: > 0 } spawnPos)
				{
					Random.State oldState = Random.state;
					Random.InitState(UserInfo.GetLocalUser().NetworkUserId.GetStableHashCode());
					int index = Random.Range(0, spawnPos.Count - 1);
					Random.state = oldState;
					pos = new Vector3(spawnPos[index].x, spawnPos[index].y, spawnPos[index].z);
					return true;
				}
			}
			catch (SerializationException)
			{
			}

			pos = Vector3.zero;
			return false;
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> _instructions, ILGenerator ilg)
		{
			MethodInfo LocationIconGetter = AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.GetLocationIcon));
			FieldInfo startLocation = AccessTools.DeclaredField(typeof(Game), nameof(Game.m_StartLocation));

			List<CodeInstruction> instructions = _instructions.ToList();
			for (int i = 0; i < instructions.Count; ++i)
			{
				if (i < instructions.Count - 4 && instructions[i].opcode == OpCodes.Callvirt && instructions[i].OperandIs(LocationIconGetter) && instructions[i - 2].opcode == OpCodes.Ldfld && instructions[i - 2].OperandIs(startLocation))
				{
					int callStart = i;
					MethodInfo zoneSystemGetter = AccessTools.DeclaredPropertyGetter(typeof(ZoneSystem), nameof(ZoneSystem.instance));
					while (instructions[callStart].opcode != OpCodes.Call || !instructions[callStart].OperandIs(zoneSystemGetter))
					{
						--callStart;
					}

					CodeInstruction afterCondition = instructions.Skip(i).SkipWhile(instr => instr.opcode.FlowControl != FlowControl.Cond_Branch).Skip(1).First();
					Label label = ilg.DefineLabel();
					afterCondition.labels.Add(label);

					List<Label> callStartLabels = instructions[callStart].labels;

					instructions.InsertRange(callStart, new[]
					{
						new CodeInstruction(OpCodes.Nop) { labels = new List<Label>(callStartLabels) },
						instructions[i - 1], // location save target
						new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ReplaceSpawnPoint), nameof(CheckCustomSpawnPoint))),
						new CodeInstruction(OpCodes.Brtrue, label),
					});

					callStartLabels.Clear();

					break;
				}
			}

			return instructions;
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.CreateTombStone))]
	private class PreserveItemsOnHardcoreModeDeath
	{
		private static ZPackage? playerSave = null;

		[HarmonyPriority(Priority.VeryHigh)]
		private static void Prefix(Player __instance)
		{
			if (ServerCharacters.hardcoreMode.GetToggle() && iDied)
			{
				playerSave = new ZPackage();
				__instance.Save(playerSave);
				playerSave.SetPos(0);
			}
		}

		[HarmonyPriority(Priority.VeryLow)]
		private static void Postfix(Player __instance)
		{
			if (playerSave is not null && ServerCharacters.hardcoreMode.GetToggle() && iDied)
			{
				__instance.m_knownTexts.Clear();
				__instance.m_knownStations.Clear();
				__instance.Load(playerSave);
				playerSave = null;
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.UpdateRespawn))]
	private static class ChangeSpawnShoutMessage
	{
		private static string ReplaceMessage(string original) => ServerCharacters.loginMessage.Value == "I have arrived!" ? original : ServerCharacters.loginMessage.Value;

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilg)
		{
			MethodInfo sendText = AccessTools.DeclaredMethod(typeof(Chat), nameof(Chat.SendText));
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.Calls(sendText))
				{
					Label skipLabel = ilg.DefineLabel();
					Label endLabel = ilg.DefineLabel();
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ChangeSpawnShoutMessage), nameof(ReplaceMessage)));
					yield return new CodeInstruction(OpCodes.Dup);
					yield return new CodeInstruction(OpCodes.Ldstr, "");
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(string), nameof(string.Equals), new[] { typeof(string), typeof(string) }));
					yield return new CodeInstruction(OpCodes.Brtrue, skipLabel);
					yield return instruction;
					yield return new CodeInstruction(OpCodes.Br, endLabel);
					yield return new CodeInstruction(OpCodes.Pop) { labels = new List<Label> { skipLabel } };
					yield return new CodeInstruction(OpCodes.Pop);
					yield return new CodeInstruction(OpCodes.Pop);
					yield return new CodeInstruction(OpCodes.Nop) { labels = new List<Label> { endLabel } };
				}
				else
				{
					yield return instruction;
				}
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
			if (iDied && ServerCharacters.hardcoreMode.GetToggle())
			{
				__instance.m_connectionFailedError.text = "You died on a hardcore server. You can continue to use your character in singleplayer, but will have to create a new one to connect to the server.";
				__instance.m_connectionFailedPanel.SetActive(true);
				iDied = false;
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

	[HarmonyPatch(typeof(Menu), nameof(Menu.QuitGame))]
	private static class ForceSaveOnQuit
	{
		private static void Prefix()
		{
			if (ZNet.m_onlineBackend == OnlineBackendType.PlayFab)
			{
				ZNet.instance.m_haveStoped = false;
				forceSynchronousSaving = false;
				Game.instance.SavePlayerProfile(true);
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.SavePlayerProfile))]
	private class ForceSavingPosition
	{
		private static bool originalValue = false;

		[UsedImplicitly]
		private static void Prefix(Game __instance, ref bool setLogoutPoint, out bool __state)
		{
			__state = ZNet.instance.m_haveStoped;
			if (__instance.m_shuttingDown)
			{
				// Ensure PlayFab connections do *not* push the sending of player save onto the background compressing queue, but directly send it
				ZNet.instance.m_haveStoped = true;
			}

			if (ZNet.m_world == null || __instance.m_playerProfile.HaveLogoutPoint())
			{
				originalValue = true;
				return;
			}

			originalValue = setLogoutPoint;
			setLogoutPoint = true;
		}

		[UsedImplicitly]
		private static void Postfix(Game __instance)
		{
			if (!originalValue)
			{
				__instance.m_playerProfile.ClearLoguoutPoint();
			}
		}

		[UsedImplicitly]
		private static void Finalizer(bool __state)
		{
			ZNet.instance.m_haveStoped = __state;
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
				knownTexts = Player.m_localPlayer.m_knownTexts.ToDictionary(t => t.Key, t => t.Value),
			};
		}
	}

	[HarmonyPatch(typeof(Inventory), nameof(Inventory.Changed))]
	private class PatchInventoryChanged
	{
		public static bool queuedThisFrame = false;

		private static void Prefix(Inventory __instance)
		{
			if (!queuedThisFrame && __instance == Player.m_localPlayer?.m_inventory && ZNet.instance.GetServerPeer() is { } serverPeer)
			{
				queuedThisFrame = true;
				IEnumerator saveAsync()
				{
					yield return null;
					queuedThisFrame = false;

					ZPackage inventoryPackage = new();
					__instance.Save(inventoryPackage);

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

	[HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
	private class KickPlayerOnDeath
	{
		private static void Prefix()
		{
			if (ServerCharacters.hardcoreMode.GetToggle())
			{
				iDied = true;
				Game.instance.Invoke(nameof(Game.Logout), 1);
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
	private static class MonitorPlayerActivity
	{
		private static Vector3 lastPos = Vector3.zero;
		private static int counter = 0;

		private static IEnumerator MeasureActivity()
		{
			for (;;)
			{
				if (Player.m_localPlayer && lastPos != Player.m_localPlayer.transform.position)
				{
					lastPos = Player.m_localPlayer.transform.position;
					counter = 0;
				}
				else if (++counter >= ServerCharacters.afkKickTimer.Value && ServerCharacters.afkKickTimer.Value > 0 && !ZNet.m_isServer)
				{
					Game.instance.Logout();
					ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorDisconnected;
					connectionError = "You have been logged out due to inactivity.";
					counter = 0;
				}

				yield return new WaitForSeconds(60);
			}
			// ReSharper disable once IteratorNeverReturns
		}

		private static void Postfix(Player __instance)
		{
			PatchInventoryChanged.queuedThisFrame = false;
			__instance.StartCoroutine(MeasureActivity());
		}
	}

}

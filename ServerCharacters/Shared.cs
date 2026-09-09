using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace ServerCharacters;

public static class Shared
{
	private static long packageCounter = 1;

	public static IEnumerable<bool> sendCompressedDataToPeer(ZNetPeer peer, string eventname, byte[] packageArray)
	{
		MemoryStream output = new();
		using (DeflateStream deflateStream = new(output, CompressionLevel.Optimal))
		{
			deflateStream.Write(packageArray, 0, packageArray.Length);
		}
		byte[] data = output.ToArray();

		const int packageSliceSize = 250000;
		const int maximumSendQueueSize = 20000;

		IEnumerable<bool> waitForQueue()
		{
			float timeout = Time.time + 30;
			while (peer.m_socket.GetSendQueueSize() > maximumSendQueueSize)
			{
				if (Time.time > timeout)
				{
					Utils.Log($"Disconnecting {peer.m_uid}. Compressed data sending for event '{eventname}' timed out after 30 seconds.");
					peer.m_rpc.Invoke("Error", ZNet.ConnectionStatus.ErrorConnectFailed);
					ZNet.instance.Disconnect(peer);
					yield break;
				}

				yield return false;
			}
		}

		void SendPackage(ZPackage pkg)
		{
			peer.m_rpc.Invoke(eventname, pkg);
		}

		int fragments = (int)(1 + (data.LongLength - 1) / packageSliceSize);
		long packageIdentifier = ++packageCounter;
		for (int fragment = 0; fragment < fragments; ++fragment)
		{
			foreach (bool wait in waitForQueue())
			{
				yield return wait;
			}

			if (!peer.m_socket.IsConnected())
			{
				yield break;
			}

			ZPackage fragmentedPackage = new();
			fragmentedPackage.Write(packageIdentifier);
			fragmentedPackage.Write(fragment);
			fragmentedPackage.Write(fragments);
			fragmentedPackage.Write(data.Skip(packageSliceSize * fragment).Take(packageSliceSize).ToArray());
			SendPackage(fragmentedPackage);

			if (fragment != fragments - 1)
			{
				yield return true;
			}
		}
	}

	private static readonly Dictionary<string, SortedDictionary<int, byte[]>> profileCache = new();
	private static readonly List<KeyValuePair<long, string>> cacheExpirations = new(); // avoid leaking memory

	public static Action<ZRpc, ZPackage> receiveCompressedFromPeer(Action<ZRpc, byte[]> onReceived) => (sender, package) =>
	{
		cacheExpirations.RemoveAll(kv =>
		{
			if (kv.Key < DateTimeOffset.Now.Ticks)
			{
				profileCache.Remove(kv.Value);
				return true;
			}

			return false;
		});

		long uniqueIdentifier = package.ReadLong();
		string cacheKey = sender.ToString() + uniqueIdentifier;
		if (!profileCache.TryGetValue(cacheKey, out SortedDictionary<int, byte[]> dataFragments))
		{
			dataFragments = new SortedDictionary<int, byte[]>();
			profileCache[cacheKey] = dataFragments;
			cacheExpirations.Add(new KeyValuePair<long, string>(DateTimeOffset.Now.AddSeconds(60).Ticks, cacheKey));
		}

		int fragment = package.ReadInt();
		int fragments = package.ReadInt();

		dataFragments.Add(fragment, package.ReadByteArray());

		if (dataFragments.Count < fragments)
		{
			Utils.Log($"Received incomplete data from peer {Utils.GetPlayerID(sender.GetSocket().GetHostName())} - fragments {fragments}, received {dataFragments.Count}");
			return;
		}

		profileCache.Remove(cacheKey);

		MemoryStream input = new(dataFragments.Values.SelectMany(a => a).ToArray());
		MemoryStream output = new();
		using (DeflateStream deflateStream = new(input, CompressionMode.Decompress))
		{
			deflateStream.CopyTo(output);
		}

		byte[] rawProfile = output.ToArray();
		onReceived(sender, rawProfile);
	};

	public static bool LoadPlayerProfileFromBytes(this PlayerProfile profile, byte[] data)
	{
		throw new NotImplementedException("Was not patched ...");
	}

	[HarmonyPatch(typeof(Shared), nameof(LoadPlayerProfileFromBytes))]
	private static class LoadPlayerProfileLoader
	{
		// replace this.LoadPlayerDataFromDisk() by new ZPackage(data) 
		[UsedImplicitly]
		private static IEnumerable<CodeInstruction> Transpiler(ILGenerator ilGenerator)
		{
			IEnumerable<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(typeof(PlayerProfile), nameof(PlayerProfile.LoadPlayerFromDisk)), ilGenerator);

			return new CodeMatcher(instructions).Start()
				.MatchForward(false, new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(PlayerProfile), nameof(PlayerProfile.LoadPlayerDataFromDisk))))
				.RemoveInstruction()
				.Insert(
					new CodeInstruction(OpCodes.Pop),
					new CodeInstruction(OpCodes.Ldarg_1) { blocks = instructions.First().blocks }, // byte[] data
					new CodeInstruction(OpCodes.Newobj, AccessTools.DeclaredConstructor(typeof(ZPackage), new[] { typeof(byte[]) }))
				).Instructions();
		}
	}

	[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
	private static class SetAutoSaveInterval
	{
		private static void Postfix()
		{
			Game.m_saveInterval = ServerCharacters.autoSaveInterval.Value * 60;
		}
	}

	public static bool CharacterNameIsForbidden(string characterName)
	{
		return characterName.Length < 3 || characterName.Any(c => c != ' ' && c != '\'' && !char.IsLetter(c));
	}

	[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.GetSaveInfo))]
	private static class FilterServerCharacterFiles
	{
		private static bool Prefix(string path, ref bool __result)
		{
			if (path.EndsWith(".signature", StringComparison.Ordinal) || path.EndsWith(".serverbackup", StringComparison.Ordinal))
			{
				__result = false;
				return false;
			}

			return true;
		}
	}
}

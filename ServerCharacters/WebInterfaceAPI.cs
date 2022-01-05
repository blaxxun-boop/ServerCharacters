using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;
using ProtoBuf;
using Debug = UnityEngine.Debug;

namespace ServerCharacters
{
	public static class WebInterfaceAPI
	{
		private static ServerConfig serverConfig = null!;
		private static readonly HashSet<TcpClient> clients = new();

		public static void StartServer()
		{
			serverConfig = new ServerConfig
			{
				configPath = Paths.ConfigPath,
				pluginsPath = Paths.PluginPath,
				patchersPath = Paths.PatcherPluginPath,
				savePath = global::Utils.GetSaveDataPath(),
				processId = Process.GetCurrentProcess().Id,
				serverName = ZNet.m_ServerName
			};

			new Thread(Server).Start();
		}

		private static async void Server()
		{
			try
			{
				Uri.TryCreate($"tcp://{ServerCharacters.serverListenAddress.Value}", UriKind.Absolute, out Uri uri);
				TcpListener listener = new(IPAddress.Parse(uri.Host), uri.Port);
				listener.Start();

				while (true)
				{
					ProcessClientRequest(await listener.AcceptTcpClientAsync());
				}
			}
			catch (Exception e)
			{
				Debug.LogError("WebInterfaceAPI threw: " + e);
			}
		}

		[HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServer))]
		private static class Patch_ZNet_SetServer
		{
			private static void Postfix()
			{
				serverConfig.serverName = ZNet.m_ServerName;
				Broadcast("ServerConfig", serverConfig);
			}
		}

		private static void Broadcast(string command, IExtensible message)
		{
			foreach (TcpClient client in clients)
			{
				SendMessage(client, command, message);
			}
		}

		private static async void ProcessClientRequest(TcpClient client)
		{
			NetworkStream data = client.GetStream();

			clients.Add(client);

			SendMessage(client, "ServerConfig", serverConfig);
			SendMessage(client, "MaintenanceMessage", new Maintenance
			{
				maintenanceActive = ServerCharacters.maintenanceMode.GetToggle() && ServerCharacters.selfReference.tickCount > int.MaxValue / 2,
				startTime = ServerCharacters.maintenanceMode.GetToggle() ? DateTimeOffset.Now.ToUnixTimeSeconds() + ServerCharacters.selfReference.tickCount : 0
			});
			SendMessage(client, "Ready", null);

			while (true)
			{
				byte[] packageLenBuf = new byte[4];
				try
				{
					byte[] packet;
					try
					{
						int read = 0;
						while (read < 4)
						{
							int bytes = await data.ReadAsync(packageLenBuf, read, 4 - read);
							if (bytes <= 0)
							{
								break;
							}
							read += bytes;
						}
						int packageLen = BitConverter.ToInt32(packageLenBuf, 0);

						packet = new byte[packageLen];
						read = 0;
						while (read < packageLen)
						{
							int bytes = await data.ReadAsync(packet, read, packageLen - read);
							if (bytes <= 0)
							{
								break;
							}
							read += bytes;
						}
					}
					catch (IOException)
					{
						break;
					}

					if (packet.Length < 8)
					{
						break;
					}
					
					ProcessPacket(client, packet);
				}
				catch (Exception e)
				{
					await System.Console.Error.WriteAsync("WAPI: " + e);
					break;
				}
			}

			clients.Remove(client);
		}

		private static void SendMessage(TcpClient client, string? command, IExtensible? msg, int packetKey = 0)
		{
			MemoryStream outData = new();
			outData.Write(BitConverter.GetBytes(packetKey), 0, 4);
			if (packetKey == 0)
			{
				byte[] commandBytes = Encoding.UTF8.GetBytes(command!);
				outData.Write(BitConverter.GetBytes(commandBytes.Length), 0, 4);
				outData.Write(commandBytes, 0, commandBytes.Length);
			}
			MemoryStream payloadStream = new();
			if (msg != null)
			{
				Serializer.Serialize(payloadStream, msg);
			}
			outData.Write(BitConverter.GetBytes((int)payloadStream.Length), 0, 4);
			outData.Write(payloadStream.ToArray(), 0, (int)payloadStream.Length);

			MemoryStream packetStream = new();
			packetStream.Write(BitConverter.GetBytes((int)outData.Length), 0, 4);
			packetStream.Write(outData.ToArray(), 0, (int)outData.Length);
			client.GetStream().Write(packetStream.ToArray(), 0, (int)packetStream.Length);
		}

		private static void ProcessPacket(TcpClient client, byte[] packet)
		{
			int packetKey = BitConverter.ToInt32(packet, 0);
			int commandNameLen = BitConverter.ToInt32(packet, 4);
			string command = Encoding.UTF8.GetString(packet, 8, commandNameLen);
			int payloadLen = BitConverter.ToInt32(packet, 8 + commandNameLen);
			MemoryStream payload = new(packet, 12 + commandNameLen, payloadLen);

			System.Console.Error.Write("WAPI got command: " + command);

			if (typeof(Command).GetMethod(command) is { } method)
			{
				System.Console.Error.Write("WAPI processing command: " + method);

				ParameterInfo[] parameters = method.GetParameters();
				object[] args = Array.Empty<object>();
				if (parameters.Length > 0)
				{
					args = new[] { Serializer.Deserialize(parameters[0].ParameterType, payload) };
				}

				object ret = method.Invoke(null, args);
				SendMessage(client, null, (IExtensible?)ret, packetKey);
			}
		}

		private class Command
		{
			public static PlayerList GetPlayerList() => ExecuteOnMain(Utils.GetPlayerListFromFiles);

			public static ModList GetModList()
			{
				ModList modList = new();
				modList.modLists.AddRange(BepInEx.Bootstrap.Chainloader.PluginInfos.Values.Select(plugin => new WebinterfaceMod
				{
					Guid = plugin.Metadata.GUID,
					Name = plugin.Metadata.Name,
					Version = plugin.Metadata.Version.ToString(),
					lastUpdate = ((DateTimeOffset)File.GetLastWriteTime(plugin.Location)).ToUnixTimeSeconds(),
					modPath = plugin.Location.Replace(Paths.PluginPath + Path.DirectorySeparatorChar, ""),
					configPath = plugin.Instance ? plugin.Instance.Config.ConfigFilePath.Replace(Paths.ConfigPath + Path.DirectorySeparatorChar, "") : ""
				}));
				return modList;
			}

			public static void SendIngameMessage(IngameMessage message) => ExecuteOnMain(() =>
			{
				IEnumerable<ZNetPeer> peers = message.steamIds.Count == 0 ? ZNet.instance.m_peers : message.steamIds.Select(id => ZNet.instance.GetPeerByHostName(id));
				foreach (ZNetPeer peer in peers)
				{
					peer.m_rpc.Invoke("ServerCharacters IngameMessage", message.Message);
				}
			});

			public static void KickPlayer(IngameMessage message) => ExecuteOnMain(() =>
			{
				IEnumerable<ZNetPeer> peers = message.steamIds.Count == 0 ? ZNet.instance.m_peers : message.steamIds.Select(id => ZNet.instance.GetPeerByHostName(id));
				foreach (ZNetPeer peer in peers)
				{
					peer.m_rpc.Invoke("ServerCharacters KickMessage", message.Message);
					ZNet.instance.Disconnect(peer);
				}
			});

			public static void SaveWorld()
			{
				ExecuteOnMain(() => ZNet.instance.ConsoleSave());
			}
		}

		public static void SendMaintenanceMessage(Maintenance message) => Broadcast("MaintenanceMessage", message);

		private static void ExecuteOnMain(Action action) => ThreadingHelper.SynchronizingObject.Invoke(action, Array.Empty<object>());
		private static T ExecuteOnMain<T>(Func<T> action) => (T) ThreadingHelper.SynchronizingObject.Invoke(action, Array.Empty<object>());
	}
}

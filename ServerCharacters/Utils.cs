using System;
using System.IO;
using System.Net;
using BepInEx.Configuration;

namespace ServerCharacters
{
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
	}
}

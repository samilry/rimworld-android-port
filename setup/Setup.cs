using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PortSetup
{
	/// <summary>
	/// Makes the player's own copy of the game ready for the phone, on the phone: what host/stage.sh does on the PC for
	/// the owner's copy. The tester's APK (PAYLOAD=port host/build.sh) carries the port (runtime, PortHooks, PortMobile,
	/// Harmony, the Workshop downloader) but none of the game; the player imports the game folder of their own Steam copy
	/// (MainActivity.prepareGame copies it to files/import), and SetupService runs this in a process of its own.
	///
	/// usage: PortSetup.dll &lt;external files folder&gt; [&lt;the imported game folder&gt;]
	///   with a game folder: its data goes to game/ (moved, not copied), its assemblies are kept in setup/original;
	///   without: the assemblies kept from the last import are prepared again (a new APK with other rules).
	/// Then: the assemblies built for x64 only are marked ARM64 (host/fix-arch.js), the platform rewrites are applied
	/// (patch/Patch.cs, the same code as the PC's patch.dll), and the type forwarders mods need are written.
	/// game/port-setup.txt says which rules prepared the game; setup/result.txt and setup/progress.txt tell the activity.
	/// </summary>
	internal static class Setup
	{
		private sealed class Refusal : Exception
		{
			public Refusal(string message) : base(message)
			{
			}
		}

		// Never taken from the player's folder: these come with the APK.
		private static readonly HashSet<string> Ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"PortHooks.dll", "PortMobile.dll", "0Harmony.dll", "Mono.Cecil.dll",
		};

		private static string progressPath;

		private static int Main(string[] args)
		{
			string ext = args[0];
			string root = args.Length > 1 ? args[1] : "";
			string game = Path.Combine(ext, "game");
			string runtime = Path.Combine(ext, "runtime");
			string setup = Path.Combine(ext, "setup");
			string originals = Path.Combine(setup, "original");
			progressPath = Path.Combine(setup, "progress.txt");
			File.Delete(Path.Combine(setup, "result.txt"));
			try
			{
				string[] version = File.ReadAllLines(Path.Combine(setup, "version.txt"));
				string notes = "";
				if (root != "")
				{
					notes = Import(root, game, runtime, originals, version.Length > 1 ? version[1].Trim() : "");
				}
				else if (!Directory.Exists(originals) || !Directory.EnumerateFiles(originals, "*.dll").Any())
				{
					throw new Refusal("没有找到之前导入的游戏文件，请重新导入。");
				}
				Prepare(game, runtime, setup, originals);
				string gameVersion = File.ReadAllLines(Path.Combine(game, "Version.txt"))[0].Trim();
				File.WriteAllText(Path.Combine(game, "port-setup.txt"), version[0].Trim() + "\n" + gameVersion + "\n");
				if (root != "")
				{
					// What is left of the import: the PC's programs and native libraries.
					TryDelete(Path.Combine(ext, "import"));
				}
				Result(setup, "ok", notes);
				Progress("完成");
				return 0;
			}
			catch (Refusal refusal)
			{
				Console.WriteLine("[setup] refused: " + refusal.Message);
				Result(setup, "error", refusal.Message);
				return 1;
			}
			catch (Exception e)
			{
				Console.WriteLine("[setup] failed: " + e);
				Result(setup, "error", "处理游戏文件时出错：" + e.GetType().Name + ": " + e.Message);
				return 2;
			}
		}

		/// <summary>Takes the game from the player's folder. What is returned is shown to the player (DLC found, version notes).</summary>
		private static string Import(string root, string game, string runtime, string originals, string supported)
		{
			Progress("检查游戏文件");
			foreach (string needed in new[] { "Version.txt", "Assembly-CSharp.dll", "RimWorld.dll", "Assets.zip", "Data/Core" })
			{
				string path = Path.Combine(root, needed);
				if (!File.Exists(path) && !Directory.Exists(path))
				{
					throw new Refusal("所选的文件里没有完整的 RimWorld 游戏（缺少 " + needed + "）。请选择 Steam 里 RimWorld 的整个游戏文件夹。");
				}
			}
			string version = File.ReadAllLines(Path.Combine(root, "Version.txt"))[0].Trim();
			if (!version.StartsWith("1.7.", StringComparison.Ordinal))
			{
				throw new Refusal("这个移植版需要 RimWorld 1.7，所选游戏的版本是 " + version + "。");
			}
			var notes = new StringBuilder();
			notes.Append("游戏版本 ").Append(version);
			string[] expansions = { "Royalty", "Ideology", "Biotech", "Anomaly", "Odyssey" };
			string[] names = { "皇权", "文化", "生物科技", "异象", "奥德赛" };
			var found = new List<string>();
			for (int i = 0; i < expansions.Length; i++)
			{
				if (Directory.Exists(Path.Combine(root, "Data", expansions[i])))
				{
					found.Add(names[i]);
				}
			}
			notes.Append("，DLC：").Append(found.Count > 0 ? string.Join("、", found) : "无");
			if (supported != "" && !version.StartsWith(supported, StringComparison.Ordinal))
			{
				notes.Append("\n注意：这个移植版是按 ").Append(supported).Append(" 做的，你的游戏版本不同，可能有地方不正常。");
			}

			Progress("移动游戏数据");
			Directory.CreateDirectory(game);
			foreach (string folder in new[] { "Data", "Rendering" })
			{
				string from = Path.Combine(root, folder), to = Path.Combine(game, folder);
				TryDelete(to);
				if (Directory.Exists(from))
				{
					Directory.Move(from, to);
				}
			}
			foreach (string file in new[] { "Assets.zip", "Version.txt", "RimWorld.runtimeconfig.json", "RimWorld.deps.json" })
			{
				string from = Path.Combine(root, file);
				if (File.Exists(from))
				{
					File.Move(from, Path.Combine(game, file), true);
				}
			}

			Progress("挑出游戏程序集");
			TryDelete(originals);
			Directory.CreateDirectory(originals);
			int kept = 0;
			foreach (string file in Directory.GetFiles(root, "*.dll"))
			{
				string name = Path.GetFileName(file);
				// The framework comes from the runtime pack; Windows' native libraries and debugging helpers are no use here.
				if (Ours.Contains(name) || File.Exists(Path.Combine(runtime, name)) || name.Contains(".Native.") || name.StartsWith("Microsoft.DiaSymReader", StringComparison.OrdinalIgnoreCase) || !IsManaged(file))
				{
					continue;
				}
				File.Move(file, Path.Combine(originals, name), true);
				kept++;
			}
			Console.WriteLine("[setup] " + version + ": " + kept + " assemblies kept, " + string.Join(", ", found));
			return notes.ToString();
		}

		/// <summary>The kept assemblies, made fit for the phone, into game/; then the forwarders.</summary>
		private static void Prepare(string game, string runtime, string setup, string originals)
		{
			Progress("改写游戏程序集（约一分钟）");
			string stage = Path.Combine(setup, "stage");
			TryDelete(stage);
			Directory.CreateDirectory(stage);
			foreach (string file in Directory.GetFiles(originals, "*.dll"))
			{
				string copy = Path.Combine(stage, Path.GetFileName(file));
				File.Copy(file, copy, true);
				MarkArm64(copy);
			}
			// The rewrites look up the port's PortPlatform in PortHooks.dll, which is in game/. Each assembly is shown as it
			// is taken (a tester's vivo stood at this step for minutes: which one it was is the first thing to know).
			Patch.FileStarted = (name, n, count) => Progress("改写游戏程序集（" + n + "/" + count + "）：" + name);
			int code = Patch.Main(new[] { stage, runtime, game });
			Patch.FileStarted = null;
			if (code != 0)
			{
				throw new Exception("the rewrites ended with " + code);
			}
			foreach (string file in Directory.GetFiles(stage, "*.dll"))
			{
				File.Move(file, Path.Combine(game, Path.GetFileName(file)), true);
			}
			TryDelete(stage);

			Progress("生成 mod 用的 Unity 转发程序集");
			string[] forwarders = File.ReadAllLines(Path.Combine(setup, "forwarders.txt")).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
			Patch.WriteForwarders(Path.Combine(game, "UnityStubs.dll"), game, forwarders);
		}

		/// <summary>A .NET assembly (it has a CLI header), not a native library.</summary>
		private static bool IsManaged(string file)
		{
			try
			{
				using var stream = File.OpenRead(file);
				using var reader = new BinaryReader(stream);
				if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5a4d)
				{
					return false;
				}
				stream.Position = 0x3c;
				int pe = reader.ReadInt32();
				stream.Position = pe;
				if (reader.ReadUInt32() != 0x4550)
				{
					return false;
				}
				stream.Position = pe + 24;
				bool pe32Plus = reader.ReadUInt16() == 0x20b;
				stream.Position = pe + 24 + (pe32Plus ? 112 : 96) + 14 * 8 + 4;
				return reader.ReadUInt32() > 0;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>
		/// Some of the game's assemblies are built for x64 only (PE machine AMD64), which this runtime refuses on arm64;
		/// they are pure IL, so the machine field is all that matters (host/fix-arch.js does the same on the PC).
		/// </summary>
		private static void MarkArm64(string file)
		{
			using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite);
			using var reader = new BinaryReader(stream);
			stream.Position = 0x3c;
			int pe = reader.ReadInt32();
			stream.Position = pe + 4;
			if (reader.ReadUInt16() == 0x8664)
			{
				stream.Position = pe + 4;
				stream.Write(BitConverter.GetBytes((ushort)0xaa64), 0, 2);
				Console.WriteLine("[setup] " + Path.GetFileName(file) + ": AMD64 -> ARM64");
			}
		}

		private static void Progress(string step)
		{
			Console.WriteLine("[setup] " + step);
			try
			{
				File.WriteAllText(progressPath, step);
			}
			catch (Exception)
			{
			}
		}

		private static void Result(string setup, string state, string message)
		{
			File.WriteAllText(Path.Combine(setup, "result.txt"), state + "\n" + message);
		}

		private static void TryDelete(string path)
		{
			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, true);
				}
				else if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[setup] could not delete " + path + ": " + e.Message);
			}
		}
	}
}

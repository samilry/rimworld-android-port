using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace PortHooks
{
	/// <summary>
	/// For measuring only (mobile.txt inlineWatch=1): every method the compiler copies into another (the runtime's
	/// MethodJitInliningSucceeded events), so that at the main menu the methods Harmony patched can be looked up among
	/// them. A patched method is not copied into code compiled after it was patched, so each one found was copied in
	/// before: where that copy runs, the patch is skipped. Tier 0 copies nothing; the tiers that do come from call
	/// counts, which the runtime starts 100 ms after the compiler last met a new method (TC_CallCountingDelayMs). This
	/// tells how many patches a change of those settings would cost. It slows the start (every inlining decision is an
	/// event). Measured 2026-10-23 (OnePlus, 137 mods, 2389 patched methods, at the main menu): as the runtime comes,
	/// 8 patched methods in 36-37 places (Rand.get_Int/PushState/PopState/set_Seed, ThingFilter.Allows, the loading
	/// screen's Widgets.DrawShadowAround/DrawAtlas into LongEventsOnGUI, Texture2D.EnsureResident); with
	/// TC_CallCountingDelayMs=0 9 in 89 places (Rand.get_Int alone 63); with TieredPGO=0 3 in 7 (host.c sets that now).
	/// </summary>
	public static class InlineWatch
	{

		private sealed class Listener : EventListener
		{
			protected override void OnEventSourceCreated(EventSource source)
			{
				if (source.Name == "Microsoft-Windows-DotNETRuntime")
				{
					// JitTracing: the inlining and tail call decisions.
					EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1000);
				}
			}

			protected override void OnEventWritten(EventWrittenEventArgs e)
			{
				if (e.EventName != "MethodJitInliningSucceeded" || e.Payload == null || e.PayloadNames == null)
				{
					return;
				}
				Interlocked.Increment(ref events);
				string inlineeType = Field(e, "InlineeNamespace"), inlinee = Field(e, "InlineeName");
				if (inlineeType.StartsWith("System.", StringComparison.Ordinal) || inlineeType.StartsWith("Microsoft.", StringComparison.Ordinal) || inlineeType.StartsWith("Internal.", StringComparison.Ordinal))
				{
					return;
				}
				string intoType = Field(e, "MethodBeingCompiledNamespace"), into = Field(e, "MethodBeingCompiledName");
				// Where it was copied into, and whether that is a method made at run time (Harmony's replacements).
				copies.GetOrAdd(inlineeType + "::" + inlinee, _ => new ConcurrentDictionary<string, bool>())[intoType + "::" + into] = intoType.Length == 0 || intoType == "dynamicClass";
			}

			private static string Field(EventWrittenEventArgs e, string name)
			{
				int i = e.PayloadNames.IndexOf(name);
				return i >= 0 && e.Payload[i] is string s ? s : "";
			}
		}

		private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> copies = new ConcurrentDictionary<string, ConcurrentDictionary<string, bool>>();
		private static Listener listener;
		private static long events;

		/// <summary>From the startup hook, before the game's assemblies are loaded.</summary>
		public static void Start()
		{
			if (Textures.Setting("inlineWatch") != "1")
			{
				return;
			}
			try
			{
				listener = new Listener();
				Console.WriteLine("[port17] inline watch: listening to the compiler's inlining (for measuring; slows the start)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] inline watch: not started: " + e.Message);
			}
		}

		/// <summary>From PortMobile at the main menu: the patched methods that had been copied into other code before.</summary>
		public static void Report(string when, IEnumerable<MethodBase> patched)
		{
			if (listener == null)
			{
				return;
			}
			try
			{
				var patchedNames = new HashSet<string>();
				foreach (MethodBase method in patched)
				{
					if (method.DeclaringType != null)
					{
						patchedNames.Add(method.DeclaringType.FullName + "::" + method.Name);
					}
				}
				int intoStatic = 0, intoDynamic = 0, places = 0;
				var samples = new List<string>();
				foreach (KeyValuePair<string, ConcurrentDictionary<string, bool>> pair in copies)
				{
					if (!patchedNames.Contains(pair.Key))
					{
						continue;
					}
					// Code that is itself patched now has been made again from its IL since, without the copy.
					List<string> into = pair.Value.Where(v => !v.Value && !patchedNames.Contains(v.Key)).Select(v => v.Key).ToList();
					if (into.Count == 0)
					{
						if (!pair.Value.IsEmpty)
						{
							intoDynamic++;
						}
						continue;
					}
					intoStatic++;
					places += into.Count;
					if (samples.Count < 25)
					{
						samples.Add(pair.Key + " (into " + into[0] + (into.Count > 1 ? " and " + (into.Count - 1) + " more" : "") + ")");
					}
				}
				Console.WriteLine("[port17] inline watch (" + when + "): " + events + " inlinings seen; of " + patchedNames.Count + " patched methods, " + intoStatic + " had been copied into compiled game or mod code (" + places
					+ " copies, where their patches are skipped), " + intoDynamic + " only into Harmony's replacement methods");
				foreach (string sample in samples)
				{
					Console.WriteLine("[port17] inline watch:   " + sample);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] inline watch: no report: " + e.Message);
			}
		}
	}
}

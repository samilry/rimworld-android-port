using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PortMobile
{
	/// <summary>
	/// Ludeon's runtime never copies a method of the game into the code Harmony makes for a patched method
	/// (WaylithClr.JitNoInlinePatchableIntoDynamicMethods, for Assembly-CSharp); the stock runtime does (inlining), and a
	/// patch put on that small method later is then skipped whenever the patched method runs. Two cases met:
	///   - the port's own patches go in at the main menu, after the mods' (Universal Turret Tweaker's patch of
	///     MapInterface.MapInterfaceUpdate held its own copy of DesignatorManager.DesignatorManagerUpdate: the cells a
	///     drag marked for the confirm button were not drawn);
	///   - a mod that patches only when it needs to (Map Preview patches Verse.Rand for its preview thread when a
	///     preview is made; Geological Landforms' patch of MapGenerator.GenerateContentsIntoMap, made at startup, had
	///     Rand.PopState in it: "Stack empty", no preview).
	/// So: every method the first time it is patched (Harmony's PatchFunctions.UpdateWrapper, which every patch goes
	/// through) is looked up among the methods patched before it. Those whose code calls it, straight or through one
	/// small method of the game that could be copied into them, get their replacement made again (by Harmony itself,
	/// with their patches as they are): a patched method is not copied into code compiled after it was patched.
	/// While the port puts its own patches in, this waits and is done once for all of them at the end.
	/// </summary>
	internal static class InlineGuard
	{
		private static readonly object sync = new object();
		private static readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();
		// A method -> the patched methods whose code calls it (or calls a small game method that calls it).
		private static readonly Dictionary<MethodBase, HashSet<MethodBase>> callers = new Dictionary<MethodBase, HashSet<MethodBase>>();
		private static readonly Dictionary<MethodBase, List<MethodBase>> calleesOf = new Dictionary<MethodBase, List<MethodBase>>();
		private static readonly Dictionary<(Module, int), MethodBase> resolved = new Dictionary<(Module, int), MethodBase>();
		private static readonly Dictionary<short, OpCode> oneByte = new Dictionary<short, OpCode>();
		private static readonly Dictionary<short, OpCode> twoByte = new Dictionary<short, OpCode>();
		private static HashSet<MethodBase> deferred = new HashSet<MethodBase>();
		private static Assembly game;
		private static MethodInfo updateWrapper, getPatchInfo;
		[ThreadStatic] private static int rebuilding;
		private static int rebuiltInAll;

		// The compiler copies a method into its callers only up to this much IL (RyuJIT's DEFAULT_MAX_INLINE_SIZE), unless the method asks for it.
		private const int MaxInlineSize = 100;

		/// <summary>First thing at the port's start: what is patched so far, and the watch on every patch from now on.</summary>
		public static void Start(Harmony harmony)
		{
			var clock = Stopwatch.StartNew();
			try
			{
				game = typeof(Verse.Thing).Assembly;
				Type functions = AccessTools.TypeByName("HarmonyLib.PatchFunctions");
				Type shared = AccessTools.TypeByName("HarmonyLib.HarmonySharedState");
				updateWrapper = AccessTools.Method(functions, "UpdateWrapper");
				getPatchInfo = AccessTools.Method(shared, "GetPatchInfo");
				if (updateWrapper == null || getPatchInfo == null)
				{
					Console.WriteLine("[mobile] inline guard: this Harmony has no PatchFunctions.UpdateWrapper / HarmonySharedState.GetPatchInfo; left out");
					updateWrapper = null;
					return;
				}
				int count = 0;
				lock (sync)
				{
					foreach (MethodBase method in Harmony.GetAllPatchedMethods())
					{
						if (patched.Add(method))
						{
							Index(method);
							count++;
						}
					}
				}
				long indexed = clock.ElapsedMilliseconds;
				harmony.Patch(updateWrapper, postfix: new HarmonyMethod(typeof(InlineGuard), nameof(AfterUpdateWrapper)));
				Console.WriteLine("[mobile] inline guard: " + count + " patched methods looked through, " + callers.Count + " methods they call (" + indexed + " ms); watching new patches");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] inline guard failed to start: " + e);
				updateWrapper = null;
			}
		}

		/// <summary>The port's patches are all in: the methods patched meanwhile have their callers made again, once.</summary>
		public static void EndOfStart()
		{
			if (updateWrapper == null)
			{
				return;
			}
			HashSet<MethodBase> batch;
			lock (sync)
			{
				batch = deferred;
				deferred = null;
			}
			Rebuild(batch, "the port's patches");
		}

		/// <summary>Postfix of Harmony's PatchFunctions.UpdateWrapper (every patch, every unpatch).</summary>
		public static void AfterUpdateWrapper(MethodBase original)
		{
			if (original == null || rebuilding > 0)
			{
				return;
			}
			HashSet<MethodBase> now;
			lock (sync)
			{
				if (!patched.Add(original))
				{
					return;
				}
				Index(original);
				if (!Inlinable(original))
				{
					return;
				}
				if (deferred != null)
				{
					deferred.Add(original);
					return;
				}
				now = new HashSet<MethodBase> { original };
			}
			Rebuild(now, null);
		}

		private static void Rebuild(HashSet<MethodBase> newlyPatched, string what)
		{
			var clock = Stopwatch.StartNew();
			var todo = new List<MethodBase>();
			lock (sync)
			{
				var seen = new HashSet<MethodBase>();
				foreach (MethodBase callee in newlyPatched)
				{
					if (callers.TryGetValue(callee, out HashSet<MethodBase> list))
					{
						foreach (MethodBase caller in list)
						{
							if (!newlyPatched.Contains(caller) && seen.Add(caller))
							{
								todo.Add(caller);
							}
						}
					}
				}
			}
			int made = 0;
			rebuilding++;
			try
			{
				foreach (MethodBase caller in todo)
				{
					try
					{
						object info = getPatchInfo.Invoke(null, new object[] { caller });
						if (info == null)
						{
							continue;
						}
						updateWrapper.Invoke(null, new object[] { caller, info });
						made++;
					}
					catch (Exception e)
					{
						Exception inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
						Console.WriteLine("[mobile] inline guard: could not make " + Name(caller) + " again: " + inner.GetType().Name + ": " + inner.Message);
					}
				}
			}
			finally
			{
				rebuilding--;
			}
			rebuiltInAll += made;
			if (todo.Count > 0 || what != null)
			{
				var names = new List<string>();
				foreach (MethodBase m in newlyPatched)
				{
					if (names.Count == 4)
					{
						names.Add("...");
						break;
					}
					names.Add(Name(m));
				}
				Console.WriteLine("[mobile] inline guard: " + (what ?? string.Join(", ", names)) + " newly patched (" + newlyPatched.Count + "); " + made + " of " + todo.Count + " patched methods that had it in them made again in " + clock.ElapsedMilliseconds + " ms (" + rebuiltInAll + " so far)");
			}
		}

		private static string Name(MethodBase method)
		{
			return method.DeclaringType?.FullName + "." + method.Name;
		}

		private static bool Inlinable(MethodBase method)
		{
			try
			{
				MethodImplAttributes flags = method.GetMethodImplementationFlags();
				if ((flags & MethodImplAttributes.NoInlining) != 0)
				{
					return false;
				}
				if ((flags & MethodImplAttributes.AggressiveInlining) != 0)
				{
					return true;
				}
				byte[] il = method.GetMethodBody()?.GetILAsByteArray();
				return il != null && il.Length <= MaxInlineSize;
			}
			catch (Exception)
			{
				return true;
			}
		}

		/// <summary>Notes a patched method under every method its code calls, and under what the small game methods among those call.</summary>
		private static void Index(MethodBase method)
		{
			foreach (MethodBase callee in Callees(method))
			{
				Add(callee, method);
				if (callee.Module.Assembly == game && !callee.IsAbstract && Inlinable(callee))
				{
					foreach (MethodBase inner in Callees(callee))
					{
						Add(inner, method);
					}
				}
			}
		}

		private static void Add(MethodBase callee, MethodBase caller)
		{
			if (!callers.TryGetValue(callee, out HashSet<MethodBase> set))
			{
				callers[callee] = set = new HashSet<MethodBase>();
			}
			set.Add(caller);
		}

		/// <summary>The methods the IL of a method calls (call, callvirt, newobj, ldftn), cached.</summary>
		private static List<MethodBase> Callees(MethodBase method)
		{
			if (calleesOf.TryGetValue(method, out List<MethodBase> known))
			{
				return known;
			}
			var list = new List<MethodBase>();
			calleesOf[method] = list;
			byte[] il;
			try
			{
				il = method.GetMethodBody()?.GetILAsByteArray();
			}
			catch (Exception)
			{
				return list;
			}
			if (il == null)
			{
				return list;
			}
			if (oneByte.Count == 0)
			{
				foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
				{
					if (field.GetValue(null) is OpCode op)
					{
						(op.Size == 1 ? oneByte : twoByte)[op.Value] = op;
					}
				}
			}
			Module module = method.Module;
			Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
			Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
			int i = 0;
			while (i < il.Length)
			{
				OpCode op;
				if (il[i] == 0xFE && i + 1 < il.Length)
				{
					if (!twoByte.TryGetValue(unchecked((short)(0xFE00 | il[i + 1])), out op))
					{
						return list;
					}
					i += 2;
				}
				else
				{
					if (!oneByte.TryGetValue(il[i], out op))
					{
						return list;
					}
					i += 1;
				}
				switch (op.OperandType)
				{
					case OperandType.InlineNone:
						break;
					case OperandType.ShortInlineBrTarget:
					case OperandType.ShortInlineI:
					case OperandType.ShortInlineVar:
						i += 1;
						break;
					case OperandType.InlineVar:
						i += 2;
						break;
					case OperandType.InlineI8:
					case OperandType.InlineR:
						i += 8;
						break;
					case OperandType.InlineSwitch:
						if (i + 4 > il.Length)
						{
							return list;
						}
						i += 4 + 4 * BitConverter.ToInt32(il, i);
						break;
					case OperandType.InlineMethod:
						if (i + 4 > il.Length)
						{
							return list;
						}
						int token = BitConverter.ToInt32(il, i);
						i += 4;
						bool shared = typeArgs == null && methodArgs == null;
						if (!shared || !resolved.TryGetValue((module, token), out MethodBase callee))
						{
							try
							{
								callee = module.ResolveMethod(token, typeArgs, methodArgs);
							}
							catch (Exception)
							{
								callee = null;
							}
							// Generic arguments of the caller make the entry differ between callers; such a callee is looked up each time.
							if (shared)
							{
								resolved[(module, token)] = callee;
							}
						}
						if (callee != null)
						{
							list.Add(callee);
						}
						break;
					default:
						i += 4;
						break;
				}
			}
			return list;
		}
	}
}

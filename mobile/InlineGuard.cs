using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PortMobile
{
	/// <summary>
	/// The port's patches go in when the main menu first comes up, after the mods have put in theirs. Harmony compiles
	/// a patched method's replacement the moment it is patched, and the .NET compiler copies small methods into the
	/// code that calls them (inlining). A replacement a mod made at startup can so hold its own copy of a small game
	/// method that the port patches only later, and the port's patch is then skipped whenever that replacement runs.
	/// (Universal Turret Tweaker patches MapInterface.MapInterfaceUpdate, which calls DesignatorManager.DesignatorManagerUpdate:
	/// the cells a drag marked for the confirm button were no longer drawn.)
	/// Here, once every port patch is in: each patched method whose own code calls a method the port patches gets its
	/// replacement made again (an empty postfix is added to it). The compiler then calls the patched method instead of
	/// copying it, since a patched method is no longer inlined into code compiled after the patch.
	/// </summary>
	internal static class InlineGuard
	{
		private const string Owner = "port17-inline";
		private static readonly Dictionary<short, OpCode> oneByte = new Dictionary<short, OpCode>();
		private static readonly Dictionary<short, OpCode> twoByte = new Dictionary<short, OpCode>();

		public static void Run()
		{
			var clock = Stopwatch.StartNew();
			try
			{
				var targets = new HashSet<MethodBase>();
				var all = new List<MethodBase>(Harmony.GetAllPatchedMethods());
				foreach (MethodBase method in all)
				{
					Patches info = Harmony.GetPatchInfo(method);
					if (info != null && System.Linq.Enumerable.Any(info.Owners, o => o.StartsWith("port17.")) && Inlinable(method))
					{
						targets.Add(method);
					}
				}
				if (targets.Count == 0)
				{
					return;
				}
				var callers = new List<(MethodBase caller, MethodBase callee)>();
				var resolved = new Dictionary<(Module, int), MethodBase>();
				foreach (MethodBase method in all)
				{
					MethodBase callee = CallsOneOf(method, targets, resolved);
					if (callee != null && callee != method)
					{
						callers.Add((method, callee));
					}
				}
				long scanned = clock.ElapsedMilliseconds;
				var harmony = new Harmony(Owner);
				var nothing = new HarmonyMethod(typeof(InlineGuard), nameof(Nothing));
				int made = 0;
				foreach (var (caller, callee) in callers)
				{
					try
					{
						harmony.Patch(caller, postfix: nothing);
						made++;
						Console.WriteLine("[mobile] inline guard: made " + Name(caller) + " again (it calls " + Name(callee) + ")");
					}
					catch (Exception e)
					{
						Console.WriteLine("[mobile] inline guard: could not make " + Name(caller) + " again: " + e.GetType().Name + ": " + e.Message);
					}
				}
				Console.WriteLine("[mobile] inline guard: " + targets.Count + " small methods patched by the port, " + all.Count + " patched in all; " + made + " of " + callers.Count + " callers made again (" + scanned + " ms to look, " + clock.ElapsedMilliseconds + " ms in all)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] inline guard failed: " + e);
			}
		}

		public static void Nothing()
		{
		}

		// The compiler copies a method into its callers only up to this much IL (RyuJIT's DEFAULT_MAX_INLINE_SIZE), unless the method asks for it.
		private const int MaxInlineSize = 100;

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

		private static string Name(MethodBase method)
		{
			return method.DeclaringType?.FullName + "." + method.Name;
		}

		/// <summary>The first method of the set that the IL of this method calls (or takes the address of), if any.</summary>
		private static MethodBase CallsOneOf(MethodBase method, HashSet<MethodBase> targets, Dictionary<(Module, int), MethodBase> resolved)
		{
			byte[] il;
			try
			{
				il = method.GetMethodBody()?.GetILAsByteArray();
			}
			catch (Exception)
			{
				return null;
			}
			if (il == null)
			{
				return null;
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
						return null;
					}
					i += 2;
				}
				else
				{
					if (!oneByte.TryGetValue(il[i], out op))
					{
						return null;
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
							return null;
						}
						i += 4 + 4 * BitConverter.ToInt32(il, i);
						break;
					case OperandType.InlineMethod:
						if (i + 4 > il.Length)
						{
							return null;
						}
						int token = BitConverter.ToInt32(il, i);
						i += 4;
						if (!resolved.TryGetValue((module, token), out MethodBase callee))
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
							if (typeArgs == null && methodArgs == null)
							{
								resolved[(module, token)] = callee;
							}
						}
						if (callee != null && targets.Contains(callee))
						{
							return callee;
						}
						break;
					default:
						i += 4;
						break;
				}
			}
			return null;
		}
	}
}

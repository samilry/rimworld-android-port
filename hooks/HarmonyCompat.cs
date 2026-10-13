using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// Makes the code of a patched method acceptable to this runtime where a transpiler written for Mono left it
	/// unbalanced: values still on the evaluation stack at a jump whose target expects fewer.
	///
	/// Two of the owner's mods do this. MinifyEverything turns "ldfld category; ldc.i4.3; bne.un L" into
	/// "ldfld category; ldc.i4.3; br L" (two values left); Humanoid Alien Races wraps ApplyBirthOutcome in a loop,
	/// "call ApplyBirthOutcome; br Loop" (the returned Thing left). Mono's JIT does not compare stack depths where
	/// control flow joins, so there the extra values are simply lost and the patches have worked for years; this
	/// runtime's JIT refuses the whole method ("Common Language Runtime detected an invalid program"), which takes the
	/// patch, or the mod that applies it in its constructor, down.
	///
	/// Harmony's instruction list for the replacement method passes through here just before it is emitted
	/// (patch/Patch.cs adds the call at the start of HarmonyLib.MethodCreatorTools.EmitCodes). The stack depth is
	/// followed along every path; where an unconditional jump or the fall into a jump target arrives with more values
	/// than the least any path brings there, "pop" instructions are put in front. A list that is balanced, which is
	/// nearly all of them, is left exactly as it is; so is one that cannot be followed with certainty (calli, a method
	/// with a variable argument list, a path that would take more than the stack holds) or cannot be repaired this way
	/// (a conditional jump with extra values).
	/// </summary>
	internal static class HarmonyCompat
	{
		private const int Unvisited = int.MinValue;

		public static void Normalize(object creator, List<CodeInstruction> codes)
		{
			if (codes == null || codes.Count == 0)
			{
				return;
			}
			var labels = new Dictionary<Label, int>();
			for (int i = 0; i < codes.Count; i++)
			{
				if (codes[i].labels != null)
				{
					foreach (Label label in codes[i].labels)
					{
						labels[label] = i;
					}
				}
			}
			int[] depth = Follow(codes, labels, out bool certain);
			if (!certain)
			{
				return;
			}
			// (index to insert before, how many pops, whether they take over the labels of the instruction there)
			var fixes = new List<(int At, int Count, bool TakeLabels)>();
			bool repairable = true;
			for (int i = 0; i < codes.Count && repairable; i++)
			{
				if (depth[i] == Unvisited)
				{
					continue;
				}
				CodeInstruction code = codes[i];
				Effect(code, out int pop, out int push);
				int after = depth[i] - pop + push;
				FlowControl flow = code.opcode.FlowControl;
				bool leave = code.opcode == OpCodes.Leave || code.opcode == OpCodes.Leave_S;
				if (flow == FlowControl.Branch && !leave && code.operand is Label target && labels.TryGetValue(target, out int t) && depth[t] != Unvisited)
				{
					if (after > depth[t])
					{
						fixes.Add((i, after - depth[t], true));
					}
				}
				else if (flow == FlowControl.Cond_Branch)
				{
					foreach (Label conditional in Targets(code))
					{
						if (labels.TryGetValue(conditional, out int c) && depth[c] != Unvisited && after > depth[c])
						{
							repairable = false; // would need a landing place of its own; not seen in any mod so far
						}
					}
				}
				if (FallsThrough(code) && i + 1 < codes.Count && depth[i + 1] != Unvisited && !BeginsHandler(codes[i + 1]) && !Ends(code))
				{
					if (after > depth[i + 1])
					{
						fixes.Add((i + 1, after - depth[i + 1], false));
					}
				}
			}
			if (fixes.Count == 0 || !repairable)
			{
				return;
			}
			var repaired = new List<CodeInstruction>(codes);
			// From the end backwards, so that the places stay right; where two fall on the same place, the pops that take
			// the jump's labels go in first and the ones for the fall into it end up in front of them.
			fixes.Sort((a, b) => a.At != b.At ? b.At.CompareTo(a.At) : b.TakeLabels.CompareTo(a.TakeLabels));
			int added = 0;
			foreach ((int at, int count, bool takeLabels) in fixes)
			{
				for (int k = 0; k < count; k++)
				{
					repaired.Insert(at, new CodeInstruction(OpCodes.Pop));
					added++;
				}
				if (takeLabels)
				{
					// Every path that reaches the jump has to pass the pops: its labels and block starts move to the first of them.
					CodeInstruction first = repaired[at], jump = repaired[at + count];
					first.labels.AddRange(jump.labels);
					jump.labels.Clear();
					first.blocks.AddRange(jump.blocks.FindAll(b => b.blockType != ExceptionBlockType.EndExceptionBlock));
					jump.blocks.RemoveAll(b => b.blockType != ExceptionBlockType.EndExceptionBlock);
				}
			}
			// Only a list that is balanced afterwards replaces the original.
			var check = new Dictionary<Label, int>();
			for (int i = 0; i < repaired.Count; i++)
			{
				foreach (Label label in repaired[i].labels)
				{
					check[label] = i;
				}
			}
			int[] again = Follow(repaired, check, out bool stillCertain);
			if (!stillCertain || !Balanced(repaired, check, again))
			{
				// Put the labels and blocks back where they were (the instruction objects are shared with the original list).
				foreach ((int at, int count, bool takeLabels) in fixes)
				{
					if (takeLabels)
					{
						CodeInstruction first = repaired[at], jump = repaired[at + count];
						jump.labels.AddRange(first.labels);
						jump.blocks.AddRange(first.blocks);
					}
				}
				return;
			}
			codes.Clear();
			codes.AddRange(repaired);
			Console.WriteLine("[port17] a patch of " + NameOf(creator) + " left values on the stack at " + fixes.Count + " jump(s); " + added + " pop(s) added (Mono drops them, this runtime refuses the method)");
		}

		private static string NameOf(object creator)
		{
			try
			{
				foreach (FieldInfo field in creator.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
				{
					object value = field.GetValue(creator);
					if (value is MethodBase method)
					{
						return method.DeclaringType?.FullName + "." + method.Name;
					}
					if (value != null && field.Name == "config")
					{
						foreach (FieldInfo inner in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
						{
							if (inner.GetValue(value) is MethodBase original)
							{
								return original.DeclaringType?.FullName + "." + original.Name;
							}
						}
					}
				}
			}
			catch (Exception)
			{
			}
			return "a method";
		}

		private static bool BeginsHandler(CodeInstruction code, out int entryDepth)
		{
			entryDepth = 0;
			if (code.blocks == null)
			{
				return false;
			}
			foreach (ExceptionBlock block in code.blocks)
			{
				switch (block.blockType)
				{
					case ExceptionBlockType.BeginCatchBlock:
					case ExceptionBlockType.BeginExceptFilterBlock:
						entryDepth = 1; // the exception
						return true;
					case ExceptionBlockType.BeginFaultBlock:
					case ExceptionBlockType.BeginFinallyBlock:
						entryDepth = 0;
						return true;
				}
			}
			return false;
		}

		private static bool BeginsHandler(CodeInstruction code)
		{
			return BeginsHandler(code, out int _);
		}

		private static bool Ends(CodeInstruction code)
		{
			return code.blocks != null && code.blocks.Exists(b => b.blockType == ExceptionBlockType.EndExceptionBlock);
		}

		private static bool FallsThrough(CodeInstruction code)
		{
			switch (code.opcode.FlowControl)
			{
				case FlowControl.Branch:
				case FlowControl.Return:
				case FlowControl.Throw:
					return false;
				default:
					return true;
			}
		}

		private static IEnumerable<Label> Targets(CodeInstruction code)
		{
			if (code.operand is Label one)
			{
				yield return one;
			}
			else if (code.operand is Label[] many)
			{
				foreach (Label label in many)
				{
					yield return label;
				}
			}
		}

		/// <summary>What an instruction takes from and puts on the stack; false when that cannot be told.</summary>
		private static bool Effect(CodeInstruction code, out int pop, out int push)
		{
			pop = 0;
			push = 0;
			OpCode op = code.opcode;
			if (op == OpCodes.Calli)
			{
				return false;
			}
			if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
			{
				if (!(code.operand is MethodBase method) || (method.CallingConvention & CallingConventions.VarArgs) != 0)
				{
					return false;
				}
				pop = method.GetParameters().Length;
				if (op == OpCodes.Newobj)
				{
					push = 1;
				}
				else
				{
					if (!method.IsStatic)
					{
						pop++;
					}
					push = method is MethodInfo info && info.ReturnType != typeof(void) ? 1 : 0;
				}
				return true;
			}
			if (op == OpCodes.Ret)
			{
				return true; // whatever is there is the return value; nothing follows
			}
			switch (op.StackBehaviourPop)
			{
				case StackBehaviour.Pop0: pop = 0; break;
				case StackBehaviour.Pop1:
				case StackBehaviour.Popi:
				case StackBehaviour.Popref: pop = 1; break;
				case StackBehaviour.Pop1_pop1:
				case StackBehaviour.Popi_pop1:
				case StackBehaviour.Popi_popi:
				case StackBehaviour.Popi_popi8:
				case StackBehaviour.Popi_popr4:
				case StackBehaviour.Popi_popr8:
				case StackBehaviour.Popref_pop1:
				case StackBehaviour.Popref_popi: pop = 2; break;
				case StackBehaviour.Popi_popi_popi:
				case StackBehaviour.Popref_popi_popi:
				case StackBehaviour.Popref_popi_popi8:
				case StackBehaviour.Popref_popi_popr4:
				case StackBehaviour.Popref_popi_popr8:
				case StackBehaviour.Popref_popi_popref:
				case StackBehaviour.Popref_popi_pop1: pop = 3; break;
				default: return false; // Varpop of something that is not a call
			}
			switch (op.StackBehaviourPush)
			{
				case StackBehaviour.Push0: push = 0; break;
				case StackBehaviour.Push1:
				case StackBehaviour.Pushi:
				case StackBehaviour.Pushi8:
				case StackBehaviour.Pushr4:
				case StackBehaviour.Pushr8:
				case StackBehaviour.Pushref: push = 1; break;
				case StackBehaviour.Push1_push1: push = 2; break;
				default: return false;
			}
			return true;
		}

		/// <summary>
		/// The least stack depth with which each instruction is reached (Unvisited for code no path leads to), starting
		/// with an empty stack at the first instruction and with the exception, or nothing, at the start of each handler.
		/// certain is false when the list cannot be followed.
		/// </summary>
		private static int[] Follow(List<CodeInstruction> codes, Dictionary<Label, int> labels, out bool certain)
		{
			certain = true;
			var depth = new int[codes.Count];
			for (int i = 0; i < depth.Length; i++)
			{
				depth[i] = Unvisited;
			}
			var work = new Stack<(int Index, int Depth)>();
			for (int i = codes.Count - 1; i >= 0; i--)
			{
				if (BeginsHandler(codes[i], out int entry))
				{
					work.Push((i, entry));
				}
			}
			work.Push((0, 0));
			int steps = 0;
			while (work.Count > 0)
			{
				(int index, int d) = work.Pop();
				if (index < 0 || index >= codes.Count)
				{
					continue;
				}
				if (++steps > 4000000)
				{
					certain = false;
					return depth;
				}
				CodeInstruction code = codes[index];
				if (BeginsHandler(code, out int fixedEntry))
				{
					d = fixedEntry;
				}
				if (depth[index] != Unvisited && depth[index] <= d)
				{
					continue;
				}
				depth[index] = d;
				if (!Effect(code, out int pop, out int push) || d < pop)
				{
					certain = false;
					return depth;
				}
				int after = d - pop + push;
				OpCode op = code.opcode;
				bool leave = op == OpCodes.Leave || op == OpCodes.Leave_S;
				switch (op.FlowControl)
				{
					case FlowControl.Branch:
						foreach (Label target in Targets(code))
						{
							if (labels.TryGetValue(target, out int t))
							{
								work.Push((t, leave ? 0 : after));
							}
						}
						break;
					case FlowControl.Cond_Branch:
						foreach (Label target in Targets(code))
						{
							if (labels.TryGetValue(target, out int t))
							{
								work.Push((t, after));
							}
						}
						break;
				}
				if (Ends(code))
				{
					// The block is left through the jump the emitter adds here: what follows starts with an empty stack.
					work.Push((index + 1, 0));
				}
				else if (FallsThrough(code) && index + 1 < codes.Count && !BeginsHandler(codes[index + 1]))
				{
					work.Push((index + 1, after));
				}
			}
			return depth;
		}

		/// <summary>Whether every jump and every fall into the next instruction arrives with the depth found for its target.</summary>
		private static bool Balanced(List<CodeInstruction> codes, Dictionary<Label, int> labels, int[] depth)
		{
			for (int i = 0; i < codes.Count; i++)
			{
				if (depth[i] == Unvisited)
				{
					continue;
				}
				CodeInstruction code = codes[i];
				if (!Effect(code, out int pop, out int push))
				{
					return false;
				}
				int after = depth[i] - pop + push;
				bool leave = code.opcode == OpCodes.Leave || code.opcode == OpCodes.Leave_S;
				FlowControl flow = code.opcode.FlowControl;
				if ((flow == FlowControl.Branch && !leave) || flow == FlowControl.Cond_Branch)
				{
					foreach (Label target in Targets(code))
					{
						if (labels.TryGetValue(target, out int t) && depth[t] != Unvisited && depth[t] != after)
						{
							return false;
						}
					}
				}
				if (FallsThrough(code) && !Ends(code) && i + 1 < codes.Count && !BeginsHandler(codes[i + 1]) && depth[i + 1] != Unvisited && depth[i + 1] != after)
				{
					return false;
				}
			}
			return true;
		}
	}
}

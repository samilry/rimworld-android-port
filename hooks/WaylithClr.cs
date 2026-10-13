using System;
using System.Reflection;

/// <summary>
/// Stand-ins for the types Ludeon's runtime fork ("WaylithClr", living in its System.Runtime) adds for the engine's
/// profiler: live method-body replacement for instrumentation, and allocation/pause events. The stock runtime has
/// none of that, so they report "not supported" and the profiler keeps its instrumentation off. patch/Patch.cs
/// retargets the engine's references from System.Runtime to this assembly.
/// </summary>
namespace WaylithClr
{
	public static class MethodBodyUpdater
	{
		public static bool IsSupported => false;

		public static int MetadataUpdateVersion => 0;

		public static int GetTokenForLocals(MethodBase method, byte[] localsSignature)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int GetTokenFor(MethodBase anchor, Type type)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int GetTokenFor(MethodBase anchor, MethodBase method)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int[] GetUpdatedMethodTokens(Module module)
		{
			return Array.Empty<int>();
		}

		public static void ReplaceMethodBodies(ReadOnlySpan<MethodBase> methods, ReadOnlySpan<byte> bodies, ReadOnlySpan<int> bodyOffsets, ReadOnlySpan<ILOffsetMapEntry> offsetMaps, ReadOnlySpan<int> offsetMapOffsets)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static void ReplaceMethodBody(MethodBase method, ReadOnlySpan<byte> body, ReadOnlySpan<ILOffsetMapEntry> offsetMap)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static void Revert(ReadOnlySpan<MethodBase> methods)
		{
		}
	}

	public struct ILOffsetMapEntry
	{
		public int OriginalOffset;
		public int NewOffset;

		public ILOffsetMapEntry(int originalOffset, int newOffset)
		{
			OriginalOffset = originalOffset;
			NewOffset = newOffset;
		}
	}

	public static class SelfProfiler
	{
		public enum RuntimePauseReason
		{
			Other,
			GC,
			AppDomainShutdown,
			ReJit,
			Shutdown,
			Debugger,
			GCPrep,
			DebuggerSweep,
			Profiler
		}

		public struct RuntimePause
		{
			public long StartTimestamp;
			public long ThreadsSuspendedTimestamp;
			public long EndTimestamp;
			public RuntimePauseReason Reason;
			public int CondemnedGeneration;
			public int TriggeringThreadId;
			public long PromotedBytes;
			public long CollectedBytes;
		}

		/// <summary>Never raised: the stock runtime has no allocation callback.</summary>
		public static event Action<object, IntPtr> ObjectAllocated
		{
			add { }
			remove { }
		}

		public static int DrainRuntimePauses(Span<RuntimePause> into)
		{
			return 0;
		}
	}
}

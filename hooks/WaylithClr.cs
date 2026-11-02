using System;
using System.Collections.Generic;
using System.Reflection;

/// <summary>
/// Stand-ins for the types Ludeon's runtime fork ("WaylithClr", living in its System.Runtime) adds for the engine's
/// profiler: live method-body replacement for instrumentation, and allocation/pause events; and, since 1.7.4995, for
/// field injection, through which the game's Prepatcher layer (Waylith.Injection and RimWorldWaylith.PrepatcherCompat,
/// set up by the launcher at every start) gives mods the fields they add to the game's classes. The stock runtime has
/// none of that, so they report "not supported": the profiler keeps its instrumentation off, and the Prepatcher layer
/// finds field injection off, as on a PC whose runtime has it disabled. patch/Patch.cs retargets the engine's
/// references from System.Runtime to this assembly.
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

		// The overloads that name the module the new IL is for (the game's Prepatcher layer since 1.7.4995).
		public static int GetTokenFor(Module moduleForIL, MethodBase member)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int GetTokenForVarargCall(Module moduleForIL, MethodBase method, Type[] optionalParameterTypes)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int GetTokenFor(Module moduleForIL, FieldInfo field)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int GetTokenForDefinition(Module moduleForIL, MethodBase member)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int GetTokenForDefinition(Module moduleForIL, FieldInfo field)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int GetTokenFor(Module moduleForIL, Type type)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int GetTokenForInstantiation(Module moduleForIL, Type genericTypeDefinition)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int GetTokenFor(Module moduleForIL, string literal)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static int GetTokenForLocals(Module moduleForIL, byte[] localSignature)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}

		public static void ReplaceMethodBodies(ReadOnlySpan<MethodBase> methods, ReadOnlySpan<ReadOnlyMemory<byte>> methodBodies, ReadOnlySpan<ReadOnlyMemory<ILOffsetMapEntry>> ilOffsetMaps = default)
		{
			throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
		}
	}

	/// <summary>
	/// Ludeon's runtime adds instance fields to classes that are already loaded: a batch of fields is committed, then
	/// applied at a frame boundary, where the live objects move to the new layout. The stand-ins behave as the PC's
	/// runtime does with the feature off: a batch takes fields, refuses to be committed, and nothing is ever pending.
	/// </summary>
	public static class FieldInjection
	{
		public static bool IsSupported => false;

		/// <summary>Never changes: no class is ever laid out anew.</summary>
		public static int LayoutEpoch => 0;

		public static int PendingCount => 0;

		public static FieldInjectionApplyResult ApplyPending()
		{
			return new FieldInjectionApplyResult();
		}
	}

	public enum FieldInjectionBatchStatus
	{
		Building,
		Queued,
		Applied,
		Failed
	}

	public enum FieldInjectionRefusal
	{
		None,
		CompileGateBusy,
		DependentFrames,
		PinnedInstance,
		NoGCRegions,
		GCNotLatched,
		GCBusy
	}

	public sealed class FieldInjectionApplyResult
	{
		public IReadOnlyList<FieldInjectionBatch> Applied { get; } = Array.Empty<FieldInjectionBatch>();

		public IReadOnlyList<FieldInjectionBatch> Pending { get; } = Array.Empty<FieldInjectionBatch>();

		public IReadOnlyList<MethodBase> RejittedMethods { get; } = Array.Empty<MethodBase>();

		public IReadOnlyList<MethodBase> StaleDynamicMethods { get; } = Array.Empty<MethodBase>();

		public long MigratedInstances => 0;

		public bool AllApplied => Pending.Count == 0;

		internal FieldInjectionApplyResult()
		{
		}
	}

	public sealed class FieldInjectionBatch
	{
		private readonly List<InjectedField> fields = new List<InjectedField>();

		public IReadOnlyList<InjectedField> Fields => fields;

		/// <summary>Stays Building: Ludeon's Commit, too, refuses before it queues anything when the feature is off.</summary>
		public FieldInjectionBatchStatus Status => FieldInjectionBatchStatus.Building;

		public FieldInjectionRefusal LastRefusal => FieldInjectionRefusal.None;

		public string PendingReason => null;

		public Exception Error => null;

		public long MigratedInstances => 0;

		public InjectedField AddField(Type declaringType, string name, Type fieldType, FieldAttributes attributes = FieldAttributes.Public)
		{
			ArgumentNullException.ThrowIfNull(declaringType);
			ArgumentException.ThrowIfNullOrEmpty(name);
			ArgumentNullException.ThrowIfNull(fieldType);
			var field = new InjectedField(declaringType, name, fieldType, attributes);
			fields.Add(field);
			return field;
		}

		/// <summary>The default and the initializers are not kept: a batch is never applied here.</summary>
		public void SetDefault(InjectedField field, object value)
		{
			ArgumentNullException.ThrowIfNull(field);
			if (!fields.Contains(field))
			{
				throw new ArgumentException("WaylithClr.FieldInjection: the field does not belong to this batch.", nameof(field));
			}
		}

		public void OnMigrated(Type declaringType, Action<object> initializer)
		{
			ArgumentNullException.ThrowIfNull(declaringType);
			ArgumentNullException.ThrowIfNull(initializer);
		}

		/// <summary>Ludeon's exception and message, so that what a mod logs reads as on the PC with the feature off.</summary>
		public void Commit()
		{
			throw new NotSupportedException("WaylithClr.FieldInjection: not supported (enable with DOTNET_FieldInjection=1, requires MethodBodyUpdater and the stock regions GC).");
		}

		public FieldInjectionApplyResult CommitAndApply()
		{
			Commit();
			return FieldInjection.ApplyPending();
		}
	}

	public sealed class InjectedField
	{
		public Type DeclaringType { get; }

		public string Name { get; }

		public Type FieldType { get; }

		public FieldAttributes Attributes { get; }

		/// <summary>0 and null: the field is made only when its batch is applied.</summary>
		public int MetadataToken => 0;

		public FieldInfo Field => null;

		internal InjectedField(Type declaringType, string name, Type fieldType, FieldAttributes attributes)
		{
			DeclaringType = declaringType;
			Name = name;
			FieldType = fieldType;
			Attributes = attributes;
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

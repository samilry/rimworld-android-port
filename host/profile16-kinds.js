// For the comparison with the 1.7 port (host/perf-profile.js): the 1.6 port's main thread, time by kind of code.
// Input: "simpleperf report --comms UnityMain --sort dso,symbol -n" of a recording made by ../tools/profile.sh
// (libil2cpp.so symbolized), on standard input. Kinds:
//   game            methods of the game's assemblies, compiled ahead of time by IL2CPP (Class_Method_mHASH)
//   framework       the class library's methods in the same form (List, Dictionary, String ...), and shared generics
//   runtime         IL2CPP's own support code (il2cpp::, GC, casts, locks, metadata lookups)
//   engine          libunity.so
//   driver          the OpenGL ES driver
//   burst           lib_burst_generated.so
// usage: simpleperf report ... | node host/profile16-kinds.js
const lines = require('fs').readFileSync(0, 'utf8').split(/\r?\n/);
const kinds = {}, top = {};
let total = 0;
const framework = /^(List_1|Dictionary_2|HashSet_1|Enumerator_|EqualityComparer_1|Comparer_1|Array_|String_|Nullable_1|KeyValuePair_2|Stack_1|Queue_1|Enumerable_|Func_|Action_|Predicate_1|Comparison_1|Math_|Mathf_|Object_|Type_|RuntimeType|Buffer_|Span_1|ReadOnlySpan_1|Interlocked_|Monitor_|Thread_|ValueTuple|Tuple_|Linq|GenericEqualityComparer|ObjectEqualityComparer|DefaultComparer|ArraySortHelper|StringBuilder_|Char_|Int32_|Single_|Boolean_|Delegate_|MulticastDelegate_|Activator_|Convert_|Number_|Guid_|Random_)/;
for (const line of lines) {
  const m = /^\s*([0-9.]+)%\s+(\d+)\s+(\S+)\s+(.*)$/.exec(line);
  if (!m) continue;
  const share = Number(m[1]), dso = m[3].replace(/^.*\//, ''), symbol = m[4].trim();
  let kind;
  if (dso === 'libil2cpp.so') {
    if (/^il2cpp::|^il2cpp_|^GC_|^google::|^std::|^Il2Cpp|^os::|^utils::|^vm::|^mono|^baselib|^__|^operator|^SetupMethods|^InitializedTypeInfo|^RaiseExecutionEngine|^Class::|^MetadataCache|^GenericClass|IsInst|IsAssignableFrom/.test(symbol)) kind = 'runtime: IL2CPP support code (casts, locks, GC, metadata)';
    else if (/_gshared/.test(symbol) || framework.test(symbol)) kind = 'framework: class library and shared generics';
    else kind = 'game: its methods, compiled ahead of time';
  } else if (dso === 'libunity.so') kind = 'engine: libunity.so';
  else if (/adreno|libGLES|libgsl|libEGL/.test(dso)) kind = 'graphics driver';
  else if (dso === 'lib_burst_generated.so') kind = 'burst jobs';
  else if (dso === '[kernel.kallsyms]' || /\.ko$/.test(dso)) kind = 'kernel';
  else if (dso === '[vdso]') kind = 'reading the clock';
  else kind = 'other native (' + dso + ')';
  kinds[kind] = (kinds[kind] || 0) + share;
  total += share;
  if (kind.startsWith('runtime') || kind.startsWith('framework')) top[symbol.replace(/_m[0-9A-F]{40}/, '').slice(0, 90)] = (top[symbol.replace(/_m[0-9A-F]{40}/, '').slice(0, 90)] || 0) + share;
}
console.log('own time by kind of code (of ' + total.toFixed(1) + ' % listed):');
for (const [k, v] of Object.entries(kinds).sort((a, b) => b[1] - a[1])) console.log('  ' + (100 * v / total).toFixed(2).padStart(6) + ' %  ' + k);
console.log('\nruntime and framework functions with the most own time:');
for (const [k, v] of Object.entries(top).sort((a, b) => b[1] - a[1]).slice(0, 22)) console.log('  ' + (100 * v / total).toFixed(2).padStart(6) + ' %  ' + k);

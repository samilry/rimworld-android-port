// Patches the libcoreclr.so that goes into the APK (host/build.sh runs this on the copy in the APK's lib folder).
// usage: node host/patch-coreclr.js <libcoreclr.so>
//
// The PAL starts every thread by setting its CPU affinity to the process's (CPalThread::ThreadEntry:
// sched_getaffinity(pid) then sched_setaffinity(0, that mask)), and in this build a failure of the second call fails
// the thread's start, so the runtime's own threads, and coreclr_initialize with them, fail: HRESULT 0x8007054F
// (ERROR_INTERNAL_ERROR from PAL_InitializeCoreCLR, the synchronization manager's worker thread). Newer runtimes go on
// when the call is refused (EPERM, EACCES). A tester's Redmi Note 13 Pro (HyperOS, Android 16) failed so every time
// (2026-10-20); the phone pins an app's main thread to some cores, which its other threads may not take. Here the
// failure is passed over altogether: the thread keeps the affinity it was created with, as on a refusal upstream.
// The patch: at 0x56a3bc, "cbz w0, 0x56a2f4" (go on when sched_setaffinity returned 0) becomes "b 0x56a2f4" (go on).
//
// Reflection may write a static readonly field after its class was initialized on the PC: Ludeon's runtime fork skips
// the check when WaylithClr.AllowInitOnlyStaticWrites is on (the game's runtimeconfig turns it on) in CoreLib's
// FieldAccessor.VerifyInitOnly (patch/Patch.cs does that one) and in InvokeUtil::SetValidField, which the first
// FieldInfo.SetValue of a field and every one of a field of a struct type go through. The stock one throws
// FieldAccessException "Cannot set initonly static field ... after type ... is initialized" there. The patch: at
// 0x455254 in SetValidField, "tbnz w8, #5, 0x4557f4" (to that throw when the field's attributes have fdInitOnly, after
// the static and class-initialized tests) becomes "nop".
//
// The bytes are checked first; another build of the runtime needs the places found again (see PROGRESS.md; the names
// come from libcoreclr.so.dbg by the library's build id, host/symbolize.sh --fetch).
const fs = require('fs');

const file = process.argv[2];
if (!file) {
  console.error('usage: node host/patch-coreclr.js <libcoreclr.so>');
  process.exit(2);
}
const patches = [
  {
    name: 'affinity patch',
    at: 0x56a3bc,
    original: Buffer.from([0xc0, 0xf9, 0xff, 0x34]), // cbz w0, 0x56a2f4
    patched: Buffer.from([0xce, 0xff, 0xff, 0x17]),  // b 0x56a2f4
    done: 'a refused sched_setaffinity no longer fails a thread\'s start',
  },
  {
    name: 'initonly patch',
    at: 0x455254,
    original: Buffer.from([0x08, 0x2d, 0x28, 0x37]), // tbnz w8, #5, 0x4557f4
    patched: Buffer.from([0x1f, 0x20, 0x03, 0xd5]),  // nop
    done: 'reflection writes static readonly fields as on the PC (AllowInitOnlyStaticWrites)',
  },
];
const data = fs.readFileSync(file);
let changed = false;
for (const patch of patches) {
  const now = data.subarray(patch.at, patch.at + 4);
  if (now.equals(patch.patched)) {
    console.log('libcoreclr.so: ' + patch.name + ' already in place');
  } else if (now.equals(patch.original)) {
    patch.patched.copy(data, patch.at);
    changed = true;
    console.log('libcoreclr.so: ' + patch.done);
  } else {
    console.error('libcoreclr.so: unexpected bytes at 0x' + patch.at.toString(16) + ' (' + now.toString('hex') + '): another runtime build? The ' + patch.name + ' is not applied, nor any other.');
    process.exit(1);
  }
}
if (changed) {
  fs.writeFileSync(file, data);
}

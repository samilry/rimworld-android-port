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
//
// The patch: at 0x56a3bc, "cbz w0, 0x56a2f4" (go on when sched_setaffinity returned 0) becomes "b 0x56a2f4" (go on).
// The bytes are checked first; another build of the runtime needs the place found again (see PROGRESS.md).
const fs = require('fs');

const file = process.argv[2];
if (!file) {
  console.error('usage: node host/patch-coreclr.js <libcoreclr.so>');
  process.exit(2);
}
const at = 0x56a3bc;
const original = Buffer.from([0xc0, 0xf9, 0xff, 0x34]); // cbz w0, 0x56a2f4
const patched = Buffer.from([0xce, 0xff, 0xff, 0x17]);  // b 0x56a2f4
const data = fs.readFileSync(file);
const now = data.subarray(at, at + 4);
if (now.equals(patched)) {
  console.log('libcoreclr.so: affinity patch already in place');
} else if (now.equals(original)) {
  patched.copy(data, at);
  fs.writeFileSync(file, data);
  console.log('libcoreclr.so: a refused sched_setaffinity no longer fails a thread\'s start');
} else {
  console.error('libcoreclr.so: unexpected bytes at 0x' + at.toString(16) + ' (' + now.toString('hex') + '): another runtime build? The affinity patch is not applied.');
  process.exit(1);
}

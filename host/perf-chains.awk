# From "simpleperf dump perf.data": one line per sample, "period address address ...", the addresses being the raw
# call chain as the kernel recorded it (the sampled instruction first). simpleperf's own reports drop the frames in
# compiled (JIT) code, which have no file behind them; host/perf-profile.sh resolves them with the runtime's method map.
/^record / {
  if (line != "") print line
  line = ""
  inchain = 0
  next
}
/^  period / { line = $2; next }
/^  callchain nr=/ { inchain = 1; next }
/^  callchain:/ { inchain = 0; next }
inchain && /^    0x/ { line = line " " $1 }
END { if (line != "") print line }

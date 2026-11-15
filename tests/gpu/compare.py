"""Compares the GPU's black holo output with the CPU matcher's (both written to bin/holo-dump)."""
import sys, os
d = sys.argv[1] if len(sys.argv) > 1 else "bin/holo-dump"
bad = 0
for line in open(os.path.join(d, "cases.txt")):
    name, w, h, tw, th = line.split(); w, h, tw, th = int(w), int(h), int(tw), int(th)
    src = open(os.path.join(d, name + ".src"), "rb").read()
    cpu = open(os.path.join(d, name + ".cpu"), "rb").read()
    gpu = open(os.path.join(d, name + ".gpu"), "rb").read()
    ct = open(os.path.join(d, name + ".cputiles"), "rb").read()
    gt = open(os.path.join(d, name + ".gputiles"), "rb").read()
    diff = sum(1 for i in range(0, len(cpu), 4) if max(abs(cpu[i + c] - gpu[i + c]) for c in range(3)) > 3)
    cpu_changed = sum(1 for i in range(0, len(cpu), 4) if cpu[i:i + 3] != src[i:i + 3])
    gpu_changed = sum(1 for i in range(0, len(gpu), 4) if max(abs(gpu[i + c] - src[i + c]) for c in range(3)) > 3)
    cov = max(abs(ct[i] - gt[i]) for i in range(0, len(ct), 2))
    lum = max(abs(ct[i + 1] - gt[i + 1]) for i in range(0, len(ct), 2))
    ok = diff <= max(3, cpu_changed // 50) and cov <= 3 and lum <= 3
    bad += not ok
    print(f"{'ok  ' if ok else 'FAIL'} {name}: cpu changed {cpu_changed}, gpu changed {gpu_changed}, differ {diff} px; tile coverage diff {cov}, brightness diff {lum}")
sys.exit(1 if bad else 0)

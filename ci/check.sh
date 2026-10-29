#!/bin/sh
# Safety net run before every release: drives the app against simulated DX1 II and DX5 II
# units and requires every frame it sends to be byte-identical to what toppingctl itself
# builds for the same commands (test/expected.py imports toppingctl directly).
#   ci/check.sh <toppingctl checkout>
set -e
cd "$(dirname "$0")/.."
TC="${1:?path to a toppingctl checkout}"
mcs -nologo -out:build/DacEqTest.exe -r:build/DacEqSwitcher.exe test/WriteTest.cs
fail=0
for k in dx1 dx5; do
    T="$(mktemp -d)"
    mkdir -p "$T/eapo" "$T/data"; touch "$T/eapo/config.txt"; echo "output=2" > "$T/sim.txt"
    DACEQ_SIM=$k DACEQ_SIM_STATE="$T/sim.txt" DACEQ_SIM_WRITES="$T/writes.txt" DACEQ_EAPO_CONFIG="$T/eapo" \
        DACEQ_DATA="$T/data" HOME="$T" timeout 120 mono build/DacEqTest.exe $k > "$T/out.txt" 2>&1 || { cat "$T/out.txt"; fail=1; continue; }
    cat "$T/out.txt"
    if grep -q -v ': ok$\|confirmed\|^state\|^slots\|^Connected\|^EM6L' "$T/out.txt"; then echo "unexpected output for $k"; fail=1; fi
    python3 test/expected.py "$TC" $k > "$T/expected.txt"
    if cmp -s "$T/expected.txt" "$T/writes.txt"; then
        echo "$k: all $(wc -l < "$T/writes.txt") frames identical to toppingctl's"
    else
        echo "$k: FRAMES DIFFER from toppingctl"; diff "$T/expected.txt" "$T/writes.txt" | head -20; fail=1
    fi
done
# EQ on/off switch
mcs -nologo -out:build/DacEqTest.exe -r:build/DacEqSwitcher.exe test/ToggleTest.cs
T="$(mktemp -d)"; mkdir -p "$T/eapo" "$T/data"; touch "$T/eapo/config.txt"
DACEQ_SIM=dx1 DACEQ_SIM_STATE="$T/sim.txt" DACEQ_EAPO_CONFIG="$T/eapo" DACEQ_DATA="$T/data" HOME="$T" \
    timeout 120 mono build/DacEqTest.exe || { echo "EQ on/off test failed"; fail=1; }
# Loudness compensation against an independent ISO 226:2003 reference
mcs -nologo -out:build/DacEqTest.exe -r:build/DacEqSwitcher.exe test/LoudnessTest.cs
python3 test/loudness_ref.py | timeout 60 mono build/DacEqTest.exe || { echo "loudness test failed"; fail=1; }
mcs -nologo -out:build/DacEqTest.exe -r:build/DacEqSwitcher.exe test/LoudnessAppTest.cs
T="$(mktemp -d)"; mkdir -p "$T/eapo" "$T/data"; touch "$T/eapo/config.txt"
DACEQ_SIM=dx1 DACEQ_SIM_STATE="$T/sim.txt" DACEQ_EAPO_CONFIG="$T/eapo" DACEQ_DATA="$T/data" HOME="$T" \
    timeout 120 mono build/DacEqTest.exe || { echo "loudness app test failed"; fail=1; }
exit $fail

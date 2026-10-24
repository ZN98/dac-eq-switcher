import sys, types
sys.modules["hid"] = types.ModuleType("hid")
sys.path.insert(0, sys.argv[1])
import toppingctl as t
kind = sys.argv[2]
if kind == "dx1":
    out = [t.dx1_block_frame(t.DX1_F_HP_VOL, t.dx1_db_to_raw(-30)), t.dx1_block_frame(t.DX1_F_MUTE, t.DX1_MUTE["analog"]),
           t.frame(*t.DX1_REG_INPUT, 1), t.frame(*t.DX1_REG_FILTER, 2), t.frame(*t.DX1_REG_HIGH_GAIN, 1),
           t.frame(*t.DX1_REG_SWITCH_CFG, 1), t.frame(*t.DX1_REG_SWITCH_CFG, 0xFFFFFFFF), t.frame(*t.DX1_REG_STATE, 2),
           ]
else:
    c = t.frame(t.REG_CTRL, t.SUB_COMMIT, 1)
    out = [t.frame(t.REG_CTRL, t.SUB_VOLUME, 60), c, t.frame(t.REG_CTRL, t.SUB_GAIN, 1), c,
           t.frame(t.REG_CTRL, t.SUB_POWER, 0, b4=t.SUB_POWER_B4, crc=True),
           t.frame(0x71, 0x11, 1), c, t.frame(0x71, 0x0D, 2), c, t.frame(0x71, 0x04, 1), c, t.frame(0x71, 0x03, 1), c]
for f in out:
    print(" ".join(f"{b:02x}" for b in f[:15]))

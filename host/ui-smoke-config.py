# Writes a ModsConfig.xml whose active mods are the minimal base plus the given packageIds (host/ui-smoke-batch.sh).
import sys, re
src, dst = sys.argv[1], sys.argv[2]
mods = sys.argv[3:]
b = open(src, 'rb').read().decode('utf-8-sig')
base = ['brrainz.harmony', 'ludeon.rimworld', 'ludeon.rimworld.royalty', 'ludeon.rimworld.ideology', 'ludeon.rimworld.biotech', 'ludeon.rimworld.anomaly', 'ludeon.rimworld.odyssey', 'unlimitedhugs.hugslib']
items = ''.join('    <li>%s</li>\r\n' % m.lower() for m in base + mods)
b2 = re.sub(r'<activeMods>.*?</activeMods>', '<activeMods>\r\n' + items + '  </activeMods>', b, flags=re.S)
open(dst, 'wb').write(('﻿' + b2).encode('utf-8'))
print(len(base + mods), 'mods')

# Reads the local file entries of a truncated zip one by one (no central directory needed): a tester's log export cut off in transfer.
# usage: python -I host/zip-salvage.py <file.zip> <out dir>
import sys, struct, zlib, os
src, out = sys.argv[1], sys.argv[2]
data = open(src, 'rb').read()
pos = 0
while pos + 30 <= len(data) and data[pos:pos+4] == b'PK\x03\x04':
    ver, flag, method, t, d, crc, csize, usize, nlen, xlen = struct.unpack('<HHHHHIIIHH', data[pos+4:pos+30])
    name = data[pos+30:pos+30+nlen].decode('utf-8', 'replace')
    start = pos + 30 + nlen + xlen
    complete = True
    if method == 8:
        dec = zlib.decompressobj(-15)
        chunk = data[start:] if (flag & 8) or csize == 0 else data[start:start+csize]
        body = dec.decompress(chunk)
        complete = dec.eof
        used = len(chunk) - len(dec.unused_data)
    else:
        body = data[start:start+csize]; used = csize
        complete = start + csize <= len(data)
    path = os.path.join(out, name)
    if not name.endswith('/'):
        os.makedirs(os.path.dirname(path) or out, exist_ok=True)
        open(path, 'wb').write(body)
    print(('ok      ' if complete else 'PARTIAL ') + '%9d %s' % (len(body), name))
    if not complete: break
    pos = start + used
    if flag & 8:
        if data[pos:pos+4] == b'PK\x07\x08': pos += 16
        else: pos += 12

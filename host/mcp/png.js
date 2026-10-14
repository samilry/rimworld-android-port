// Just enough PNG for screenshots: decode 8-bit RGB/RGBA (what screencap writes), crop and shrink (box filter), encode RGB.
const zlib = require('zlib');

const SIGNATURE = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);

function decode(buffer) {
  if (!buffer.subarray(0, 8).equals(SIGNATURE)) throw new Error('not a PNG');
  let pos = 8, width = 0, height = 0, colorType = 0, bitDepth = 0, interlace = 0;
  const idat = [];
  while (pos < buffer.length) {
    const length = buffer.readUInt32BE(pos);
    const type = buffer.toString('ascii', pos + 4, pos + 8);
    const data = buffer.subarray(pos + 8, pos + 8 + length);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
      interlace = data[12];
    } else if (type === 'IDAT') {
      idat.push(data);
    } else if (type === 'IEND') {
      break;
    }
    pos += 12 + length;
  }
  if (bitDepth !== 8 || (colorType !== 6 && colorType !== 2) || interlace !== 0) {
    throw new Error('unsupported PNG (depth ' + bitDepth + ', colour type ' + colorType + ', interlace ' + interlace + ')');
  }
  const channels = colorType === 6 ? 4 : 3;
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  const pixels = Buffer.alloc(width * height * 3);
  let previous = Buffer.alloc(stride);
  let row = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let i = 0; i < stride; i++) {
      const a = i >= channels ? row[i - channels] : 0;
      const b = previous[i];
      const c = i >= channels ? previous[i - channels] : 0;
      let value = line[i];
      switch (filter) {
        case 1: value += a; break;
        case 2: value += b; break;
        case 3: value += (a + b) >> 1; break;
        case 4: {
          const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
          value += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
          break;
        }
      }
      row[i] = value & 255;
    }
    for (let x = 0; x < width; x++) {
      pixels[(y * width + x) * 3] = row[x * channels];
      pixels[(y * width + x) * 3 + 1] = row[x * channels + 1];
      pixels[(y * width + x) * 3 + 2] = row[x * channels + 2];
    }
    const swap = previous; previous = row; row = swap;
  }
  return { width, height, pixels };
}

// Crops [x, y, w, h] (clamped) and scales by `scale` (<= 1) with a box filter.
function cropScale(image, region, scale) {
  let [x0, y0, w, h] = region || [0, 0, image.width, image.height];
  x0 = Math.max(0, Math.min(image.width - 1, Math.round(x0)));
  y0 = Math.max(0, Math.min(image.height - 1, Math.round(y0)));
  w = Math.max(1, Math.min(image.width - x0, Math.round(w)));
  h = Math.max(1, Math.min(image.height - y0, Math.round(h)));
  const ow = Math.max(1, Math.round(w * scale)), oh = Math.max(1, Math.round(h * scale));
  const out = Buffer.alloc(ow * oh * 3);
  for (let oy = 0; oy < oh; oy++) {
    const sy0 = y0 + Math.floor(oy * h / oh), sy1 = Math.max(sy0 + 1, y0 + Math.floor((oy + 1) * h / oh));
    for (let ox = 0; ox < ow; ox++) {
      const sx0 = x0 + Math.floor(ox * w / ow), sx1 = Math.max(sx0 + 1, x0 + Math.floor((ox + 1) * w / ow));
      let r = 0, g = 0, b = 0, n = 0;
      for (let sy = sy0; sy < sy1; sy++) {
        for (let sx = sx0; sx < sx1; sx++) {
          const i = (sy * image.width + sx) * 3;
          r += image.pixels[i]; g += image.pixels[i + 1]; b += image.pixels[i + 2]; n++;
        }
      }
      const o = (oy * ow + ox) * 3;
      out[o] = r / n; out[o + 1] = g / n; out[o + 2] = b / n;
    }
  }
  return { width: ow, height: oh, pixels: out, x0, y0, w, h };
}

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c;
  }
  return table;
})();

function crc32(buffer) {
  let c = -1;
  for (let i = 0; i < buffer.length; i++) c = CRC_TABLE[(c ^ buffer[i]) & 255] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

function chunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
}

function encode(image) {
  const header = Buffer.alloc(13);
  header.writeUInt32BE(image.width, 0);
  header.writeUInt32BE(image.height, 4);
  header[8] = 8; header[9] = 2; header[10] = 0; header[11] = 0; header[12] = 0;
  const stride = image.width * 3;
  const raw = Buffer.alloc((stride + 1) * image.height);
  for (let y = 0; y < image.height; y++) {
    // Filter "up": screenshots compress better with it.
    raw[y * (stride + 1)] = 2;
    for (let i = 0; i < stride; i++) {
      const above = y > 0 ? image.pixels[(y - 1) * stride + i] : 0;
      raw[y * (stride + 1) + 1 + i] = (image.pixels[y * stride + i] - above) & 255;
    }
  }
  return Buffer.concat([SIGNATURE, chunk('IHDR', header), chunk('IDAT', zlib.deflateSync(raw, { level: 6 })), chunk('IEND', Buffer.alloc(0))]);
}

module.exports = { decode, cropScale, encode };

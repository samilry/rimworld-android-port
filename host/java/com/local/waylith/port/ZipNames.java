package com.local.waylith.port;

import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.Charset;
import java.nio.charset.CharsetDecoder;
import java.nio.charset.CharsetEncoder;
import java.nio.charset.CoderResult;
import java.nio.charset.CodingErrorAction;

/**
 * The names of the entries of a zip archive, whatever wrote it.
 *
 * An archive says of each name whether it is UTF-8 (a flag); where it does not say, the name is in whatever the
 * program that packed it used. Windows' own "compressed folder" on a Chinese system writes GBK without the flag, other
 * tools write UTF-8 without it. java.util.zip decodes a flagged name as UTF-8 itself and every other one with the
 * charset it was given, and on Android a name that does not fit that charset is not refused but comes back with
 * replacement characters: a mod's folder "ChineseSimplified (...)", the language's name in Chinese in the brackets,
 * packed on Windows arrived with eight replacement characters there, and the game did not find the mod's translation.
 *
 * So the archive is read with RAW, which decodes nothing: every byte above ASCII becomes one character of the private
 * use area. A name that has such characters is one the archive did not flag; its bytes are taken back and read as
 * UTF-8 if they are valid UTF-8, as GBK otherwise. A name without them is ASCII, or was flagged and is decoded already
 * (which is why the bytes are not simply read as Latin-1: a Spanish folder name decoded from a flagged name, with its
 * n-tilde, must stay as it is).
 */
final class ZipNames
{
    private static final int BASE = 0xE000;

    static final Charset RAW = new Charset("x-port17-zip-raw", null)
    {
        @Override
        public boolean contains(Charset other)
        {
            return false;
        }

        @Override
        public CharsetDecoder newDecoder()
        {
            return new CharsetDecoder(this, 1f, 1f)
            {
                @Override
                protected CoderResult decodeLoop(ByteBuffer in, CharBuffer out)
                {
                    while (in.hasRemaining())
                    {
                        if (!out.hasRemaining())
                        {
                            return CoderResult.OVERFLOW;
                        }
                        int b = in.get() & 0xFF;
                        out.put((char) (b < 0x80 ? b : BASE + b));
                    }
                    return CoderResult.UNDERFLOW;
                }
            };
        }

        @Override
        public CharsetEncoder newEncoder()
        {
            return new CharsetEncoder(this, 1f, 1f)
            {
                @Override
                protected CoderResult encodeLoop(CharBuffer in, ByteBuffer out)
                {
                    while (in.hasRemaining())
                    {
                        if (!out.hasRemaining())
                        {
                            return CoderResult.OVERFLOW;
                        }
                        char c = in.get();
                        if (c < 0x80)
                        {
                            out.put((byte) c);
                        }
                        else if (c >= BASE + 0x80 && c <= BASE + 0xFF)
                        {
                            out.put((byte) (c - BASE));
                        }
                        else
                        {
                            in.position(in.position() - 1);
                            return CoderResult.unmappableForLength(1);
                        }
                    }
                    return CoderResult.UNDERFLOW;
                }
            };
        }
    };

    /** The name of an entry read with RAW, as text. */
    static String decode(String name)
    {
        boolean raw = false;
        for (int i = 0; i < name.length(); i++)
        {
            char c = name.charAt(i);
            if (c >= BASE + 0x80 && c <= BASE + 0xFF)
            {
                raw = true;
            }
            else if (c >= 0x80)
            {
                return name; // decoded by the archive's own flag
            }
        }
        if (!raw)
        {
            return name;
        }
        byte[] bytes = new byte[name.length()];
        for (int i = 0; i < bytes.length; i++)
        {
            char c = name.charAt(i);
            bytes[i] = (byte) (c < 0x80 ? c : c - BASE);
        }
        try
        {
            return Charset.forName("UTF-8").newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString();
        }
        catch (CharacterCodingException notUtf8)
        {
            return new String(bytes, Charset.forName("GBK"));
        }
    }

    private ZipNames()
    {
    }
}

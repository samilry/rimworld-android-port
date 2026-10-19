package com.local.waylith.port;

import android.app.Activity;
import android.content.res.AssetManager;
import android.util.Log;
import android.widget.Toast;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/**
 * The game's files inside the APK, for a phone they were not copied to by hand (host/build.sh with PAYLOAD=1).
 *
 * assets/payload.zip holds runtime/ (the .NET framework) and game/ (the game, the port's assemblies, the Workshop
 * downloader, an empty Mods folder); assets/payload.txt says which build it is and how many bytes it unpacks to. The
 * host calls ensure() on its runtime thread before it starts the runtime: when the APK's payload is not the one
 * unpacked last (<ext>/payload.txt), it is unpacked into the app's external files folder. Files of the same name are
 * replaced; nothing else there is touched (the player's mods, mobile.txt, the save data under home/). An args.txt
 * that is already there is kept. An APK without a payload changes nothing.
 */
public final class Payload
{
    private static final String TAG = "port17";

    private Payload()
    {
    }

    /** Null when the files are there (or the APK has none); otherwise what went wrong. */
    public static String ensure(Activity activity)
    {
        File ext = activity.getExternalFilesDir(null);
        AssetManager assets = activity.getAssets();
        String[] about;
        try
        {
            about = readAll(assets.open("payload.txt")).trim().split("\n");
        }
        catch (IOException none)
        {
            return null;
        }
        String build = about[0].trim();
        long total = about.length > 1 ? Long.parseLong(about[1].trim()) : 0;
        File marker = new File(ext, "payload.txt");
        try
        {
            if (marker.exists() && readAll(new FileInputStream(marker)).trim().split("\n")[0].trim().equals(build))
            {
                return null;
            }
        }
        catch (IOException ignored)
        {
        }
        long free = ext.getUsableSpace();
        if (total > 0 && free < total + (200L << 20))
        {
            String problem = "存储空间不足：解压游戏文件需要约 " + mb(total + (200L << 20)) + " MB，手机上只剩 " + mb(free) + " MB。";
            toast(activity, problem);
            return problem;
        }
        Log.i(TAG, "payload " + build + ": unpacking " + mb(total) + " MB into " + ext);
        toast(activity, "首次启动：正在解压游戏文件（约 " + mb(total) + " MB），需要一两分钟，请不要退出。");
        long started = System.currentTimeMillis();
        long done = 0;
        int shownPercent = 0;
        byte[] buffer = new byte[1 << 20];
        try (ZipInputStream zip = new ZipInputStream(new BufferedInputStream(assets.open("payload.zip", AssetManager.ACCESS_STREAMING), 1 << 16)))
        {
            String root = ext.getCanonicalPath() + File.separator;
            for (ZipEntry entry; (entry = zip.getNextEntry()) != null; )
            {
                File target = new File(ext, entry.getName());
                if (!target.getCanonicalPath().startsWith(root))
                {
                    continue;
                }
                if (entry.isDirectory())
                {
                    target.mkdirs();
                    continue;
                }
                if (entry.getName().equals("game/args.txt") && target.exists())
                {
                    continue;
                }
                target.getParentFile().mkdirs();
                File part = new File(target.getPath() + ".part");
                try (OutputStream out = new FileOutputStream(part))
                {
                    for (int n; (n = zip.read(buffer)) > 0; )
                    {
                        out.write(buffer, 0, n);
                        done += n;
                    }
                }
                if (!part.renameTo(target))
                {
                    target.delete();
                    if (!part.renameTo(target))
                    {
                        throw new IOException("could not put " + target + " in place");
                    }
                }
                int percent = total > 0 ? (int) (done * 100 / total) : 0;
                if (percent >= shownPercent + 20 && percent < 100)
                {
                    shownPercent = percent - percent % 20;
                    toast(activity, "正在解压游戏文件：" + shownPercent + "%");
                }
            }
        }
        catch (IOException e)
        {
            Log.e(TAG, "payload: unpacking failed", e);
            String problem = "解压游戏文件失败：" + e.getMessage();
            toast(activity, problem);
            return problem;
        }
        try (OutputStream out = new FileOutputStream(marker))
        {
            out.write((build + "\n").getBytes("UTF-8"));
        }
        catch (IOException e)
        {
            Log.e(TAG, "payload: marker not written", e);
        }
        Log.i(TAG, "payload " + build + ": " + mb(done) + " MB unpacked in " + (System.currentTimeMillis() - started) / 1000 + " s");
        toast(activity, "解压完成，正在启动游戏（第一次要多等一会儿）。");
        return null;
    }

    private static long mb(long bytes)
    {
        return bytes >> 20;
    }

    private static void toast(final Activity activity, final String text)
    {
        activity.runOnUiThread(new Runnable()
        {
            public void run()
            {
                Toast.makeText(activity, text, Toast.LENGTH_LONG).show();
            }
        });
    }

    private static String readAll(InputStream in) throws IOException
    {
        try
        {
            byte[] buffer = new byte[4096];
            StringBuilder text = new StringBuilder();
            for (int n; (n = in.read(buffer)) > 0; )
            {
                text.append(new String(buffer, 0, n, "UTF-8"));
            }
            return text.toString();
        }
        finally
        {
            in.close();
        }
    }
}

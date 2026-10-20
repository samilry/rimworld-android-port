package com.local.waylith.port;

import android.app.Activity;
import android.app.ActivityManager;
import android.app.AlertDialog;
import android.app.ApplicationExitInfo;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ShortcutInfo;
import android.content.pm.ShortcutManager;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;
import android.util.Log;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.Charset;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * Reports for a tester far away, who has no logcat and cannot open the app's folder under Android/data.
 *
 * When the game ended abnormally the last time (a crash, a native crash, being killed for memory, an ANR: Android
 * keeps the reasons, ActivityManager.getHistoricalProcessExitReasons), the next start packs a report into
 * Download/Port17/ (a zip: why it ended, for a native crash the system's tombstone decoded to text, with the last
 * lines of the process's log; the game's Player.log and the port's Port17.log of that run, the runtime's stderr, the
 * mod list, the phone) and says so in a dialog. "导出日志" on the launcher icon (a dynamic shortcut,
 * ExportLogsActivity) packs the same without a crash. API keys and tokens are masked in the text files.
 */
final class Diagnostics
{
    private static final String TAG = "port17";
    private static final Pattern SECRETS = Pattern.compile("(sk-|key=|Key=|token=|Bearer )[A-Za-z0-9_\\-\\.]{6,}");

    private Diagnostics()
    {
    }

    /** From MainActivity.onCreate: the shortcut, and a report when the previous run ended badly. */
    static void onStart(final Activity activity)
    {
        addShortcut(activity);
        if (Build.VERSION.SDK_INT < 30)
        {
            return;
        }
        new Thread(new Runnable()
        {
            @Override
            public void run()
            {
                try
                {
                    final String found = checkLastExit(activity);
                    if (found == null)
                    {
                        return;
                    }
                    final String where = export(activity, found);
                    activity.runOnUiThread(new Runnable()
                    {
                        @Override
                        public void run()
                        {
                            if (activity.isFinishing())
                            {
                                return;
                            }
                            new AlertDialog.Builder(activity)
                                .setTitle("上次游戏异常退出")
                                .setMessage(firstLine(found) + "\n\n"
                                    + (where != null ? "诊断报告已保存到：" + where + "\n\n请把这个文件发给作者（里面是游戏日志和手机型号，API 密钥已打码）。"
                                    : "诊断报告没能保存。"))
                                .setPositiveButton("好的", null)
                                .show();
                        }
                    });
                }
                catch (Throwable t)
                {
                    Log.e(TAG, "diagnostics failed", t);
                }
            }
        }, "Diagnostics").start();
    }

    private static String firstLine(String text)
    {
        int end = text.indexOf('\n');
        return end < 0 ? text : text.substring(0, end);
    }

    private static void addShortcut(Context context)
    {
        if (Build.VERSION.SDK_INT < 26)
        {
            return;
        }
        try
        {
            ShortcutManager shortcuts = context.getSystemService(ShortcutManager.class);
            Intent intent = new Intent(Intent.ACTION_VIEW).setClassName(context.getPackageName(), ExportLogsActivity.class.getName());
            ShortcutInfo info = new ShortcutInfo.Builder(context, "export-logs")
                .setShortLabel("导出日志")
                .setLongLabel("导出诊断日志")
                .setIntent(intent)
                .build();
            shortcuts.setDynamicShortcuts(Collections.singletonList(info));
        }
        catch (Throwable t)
        {
            Log.w(TAG, "no export shortcut: " + t);
        }
    }

    // ---- why the game ended ----

    private static String reasonName(int reason)
    {
        switch (reason)
        {
            case ApplicationExitInfo.REASON_ANR: return "ANR（无响应）";
            case ApplicationExitInfo.REASON_CRASH: return "CRASH（Java 崩溃）";
            case ApplicationExitInfo.REASON_CRASH_NATIVE: return "CRASH_NATIVE（原生崩溃）";
            case ApplicationExitInfo.REASON_DEPENDENCY_DIED: return "DEPENDENCY_DIED";
            case ApplicationExitInfo.REASON_EXCESSIVE_RESOURCE_USAGE: return "EXCESSIVE_RESOURCE_USAGE（资源占用过多）";
            case ApplicationExitInfo.REASON_EXIT_SELF: return "EXIT_SELF（自己退出）";
            case ApplicationExitInfo.REASON_INITIALIZATION_FAILURE: return "INITIALIZATION_FAILURE";
            case ApplicationExitInfo.REASON_LOW_MEMORY: return "LOW_MEMORY（内存不足被杀）";
            case ApplicationExitInfo.REASON_OTHER: return "OTHER（被系统结束）";
            case ApplicationExitInfo.REASON_PERMISSION_CHANGE: return "PERMISSION_CHANGE";
            case ApplicationExitInfo.REASON_SIGNALED: return "SIGNALED（被信号结束）";
            case ApplicationExitInfo.REASON_USER_REQUESTED: return "USER_REQUESTED（用户结束）";
            case ApplicationExitInfo.REASON_USER_STOPPED: return "USER_STOPPED";
            default: return "reason " + reason;
        }
    }

    private static boolean abnormal(ApplicationExitInfo exit)
    {
        if (exit.getDescription() != null && exit.getDescription().startsWith("exit_self"))
        {
            // The app ended itself (Process.killProcess: the player left the import, quit the game, restarted it).
            return false;
        }
        switch (exit.getReason())
        {
            case ApplicationExitInfo.REASON_ANR:
            case ApplicationExitInfo.REASON_CRASH:
            case ApplicationExitInfo.REASON_CRASH_NATIVE:
            case ApplicationExitInfo.REASON_EXCESSIVE_RESOURCE_USAGE:
            case ApplicationExitInfo.REASON_INITIALIZATION_FAILURE:
                return true;
            case ApplicationExitInfo.REASON_LOW_MEMORY:
                // Out of memory on the screen (or while importing, a foreground service) is a problem; in the background
                // it is how Android makes room (a tester's phone ended the app in the file chooser again and again, and
                // every return showed a crash report).
                return exit.getImportance() <= ActivityManager.RunningAppProcessInfo.IMPORTANCE_PERCEPTIBLE;
            case ApplicationExitInfo.REASON_SIGNALED:
            case ApplicationExitInfo.REASON_OTHER:
                // Killed while on the screen: not something the player chose. (In the background the system ends apps all the time.)
                return exit.getImportance() <= ActivityManager.RunningAppProcessInfo.IMPORTANCE_VISIBLE;
            default:
                return false;
        }
    }

    /**
     * A line in logs/import.txt: what the start and the import of the player's game did (MainActivity, SetupService),
     * which otherwise only logcat sees. Goes into every log export.
     */
    static synchronized void trail(Context context, String text)
    {
        try
        {
            File dir = new File(context.getExternalFilesDir(null), "logs");
            dir.mkdirs();
            File file = new File(dir, "import.txt");
            if (file.length() > (256 << 10))
            {
                file.renameTo(new File(dir, "import-prev.txt"));
            }
            FileOutputStream out = new FileOutputStream(file, true);
            try
            {
                String stamp = new SimpleDateFormat("MM-dd HH:mm:ss", Locale.US).format(new Date());
                out.write((stamp + " [" + android.os.Process.myPid() + "] " + text + "\n").getBytes("UTF-8"));
            }
            finally
            {
                out.close();
            }
        }
        catch (Throwable ignored)
        {
        }
        Log.i(TAG, text);
    }

    /** Every exit the system remembers, of all the app's processes (newest first). */
    private static String exitHistory(Context context)
    {
        if (Build.VERSION.SDK_INT < 30)
        {
            return "（安卓 11 以下没有退出记录）";
        }
        StringBuilder text = new StringBuilder();
        try
        {
            for (ApplicationExitInfo exit : context.getSystemService(ActivityManager.class).getHistoricalProcessExitReasons(context.getPackageName(), 0, 32))
            {
                text.append(exit.getProcessName()).append("  ").append(describe(exit)).append('\n');
            }
        }
        catch (Throwable t)
        {
            text.append("读不了：").append(t).append('\n');
        }
        return text.toString();
    }

    /**
     * The app is about to end its own process (a restart after the mod list changed, the player leaving the import):
     * the exit that follows is not reported. Android 16 and older describe such an exit as "exit_self_..."; a tester's
     * Honor on Android 17 gave no description, and every restart after a change of mods came back as a crash report.
     */
    static void noteSelfExit(Context context)
    {
        context.getSharedPreferences("diagnostics", Context.MODE_PRIVATE).edit().putLong("selfExitAt", System.currentTimeMillis()).commit();
    }

    /** Whether an exit is the one the app announced (noteSelfExit): within a few seconds after it. */
    private static boolean announced(ApplicationExitInfo exit, long selfExitAt)
    {
        long after = exit.getTimestamp() - selfExitAt;
        return selfExitAt > 0 && after >= -1000 && after <= 15000;
    }

    /** How many files and megabytes a folder holds (all the way down). */
    private static String folderSize(File dir)
    {
        if (!dir.exists())
        {
            return "（无）";
        }
        long[] sum = new long[2];
        sizeOf(dir, sum, 0);
        return sum[0] + " 个文件，" + (sum[1] >> 20) + " MB";
    }

    private static void sizeOf(File file, long[] sum, int depth)
    {
        File[] children = file.listFiles();
        if (children == null)
        {
            sum[0]++;
            sum[1] += file.length();
            return;
        }
        if (depth < 24)
        {
            for (File child : children)
            {
                sizeOf(child, sum, depth + 1);
            }
        }
    }

    private static String describe(ApplicationExitInfo exit)
    {
        SimpleDateFormat format = new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US);
        return format.format(new Date(exit.getTimestamp())) + "  " + reasonName(exit.getReason())
            + "  status " + exit.getStatus() + ", importance " + exit.getImportance()
            + ", PSS " + exit.getPss() / 1024 + " MB, RSS " + exit.getRss() / 1024 + " MB"
            + (exit.getDescription() != null ? "\n    " + exit.getDescription() : "");
    }

    /** The text of the report when the newest exit of the game's process not reported yet was abnormal, else null. */
    private static String checkLastExit(Context context) throws IOException
    {
        ActivityManager manager = context.getSystemService(ActivityManager.class);
        List<ApplicationExitInfo> exits = manager.getHistoricalProcessExitReasons(context.getPackageName(), 0, 16);
        SharedPreferences prefs = context.getSharedPreferences("diagnostics", Context.MODE_PRIVATE);
        long seen = prefs.getLong("seenExit", 0);
        long selfExitAt = prefs.getLong("selfExitAt", 0);
        if (seen == 0)
        {
            // First start of this APK: exits of the last day still count (a crash of the APK this one replaced).
            seen = System.currentTimeMillis() - 24L * 3600 * 1000;
        }
        ApplicationExitInfo newest = null;
        StringBuilder history = new StringBuilder();
        long newestTime = prefs.getLong("seenExit", 0);
        for (ApplicationExitInfo exit : exits)
        {
            if (!context.getPackageName().equals(exit.getProcessName()))
            {
                continue; // the :setup process ends by killing itself
            }
            history.append(describe(exit)).append('\n');
            newestTime = Math.max(newestTime, exit.getTimestamp());
            if (exit.getTimestamp() > seen && abnormal(exit) && !announced(exit, selfExitAt) && (newest == null || exit.getTimestamp() > newest.getTimestamp()))
            {
                newest = exit;
            }
        }
        prefs.edit().putLong("seenExit", Math.max(newestTime, 1)).apply();
        if (newest == null)
        {
            return null;
        }
        StringBuilder text = new StringBuilder();
        text.append("上次退出：").append(reasonName(newest.getReason())).append('\n');
        text.append(describe(newest)).append("\n\n最近的退出记录（新的在前）：\n").append(history);
        byte[] trace = null;
        InputStream in = newest.getTraceInputStream();
        if (in != null)
        {
            trace = readAll(in, 8 << 20);
        }
        if (trace != null && newest.getReason() == ApplicationExitInfo.REASON_CRASH_NATIVE)
        {
            text.append("\n==== 原生崩溃（tombstone）====\n");
            try
            {
                text.append(Tombstone.toText(trace));
            }
            catch (Throwable t)
            {
                text.append("tombstone 解析失败：").append(t).append('\n');
            }
            lastTrace = trace;
        }
        else if (trace != null)
        {
            text.append("\n==== trace ====\n").append(new String(trace, Charset.forName("UTF-8")));
        }
        return text.toString();
    }

    /** The raw tombstone of the crash just reported, put in the zip too (the text above is decoded from it). */
    private static byte[] lastTrace;

    // ---- the zip ----

    /** Packs the report into Download/Port17 (on Android 10 and newer, through MediaStore); returns where it went, or null. */
    static String export(Context context, String exitText)
    {
        String stamp = new SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(new Date());
        String name = (exitText != null ? "崩溃报告-" : "日志-") + stamp + ".zip";
        OutputStream out = null;
        String where;
        try
        {
            if (Build.VERSION.SDK_INT >= 29)
            {
                ContentValues values = new ContentValues();
                values.put(MediaStore.MediaColumns.DISPLAY_NAME, name);
                values.put(MediaStore.MediaColumns.MIME_TYPE, "application/zip");
                values.put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/Port17");
                ContentResolver resolver = context.getContentResolver();
                Uri uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
                if (uri == null)
                {
                    return null;
                }
                out = resolver.openOutputStream(uri);
                where = "下载/Port17/" + name;
            }
            else
            {
                File dir = new File(context.getExternalFilesDir(null), "reports");
                dir.mkdirs();
                File file = new File(dir, name);
                out = new FileOutputStream(file);
                where = file.getPath();
            }
            ZipOutputStream zip = new ZipOutputStream(out);
            File ext = context.getExternalFilesDir(null);
            File logs = new File(ext, "home/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Logs");
            File data = new File(ext, "home/unity3d/Ludeon Studios/RimWorld by Ludeon Studios");
            putText(zip, "phone.txt", phone(context, ext));
            if (exitText != null)
            {
                putText(zip, "exit.txt", exitText);
                if (lastTrace != null)
                {
                    putBytes(zip, "tombstone.pb", lastTrace);
                }
            }
            for (String file : new String[] { "Player.log", "Player-prev.log", "Port17.log", "Port17-prev.log" })
            {
                putFile(zip, file, new File(logs, file));
            }
            putFile(zip, "stderr.txt", new File(ext, "logs/stderr.txt"));
            putFile(zip, "ModsConfig.xml", new File(data, "Config/ModsConfig.xml"));
            // The import of the player's game (the tester's APK): what was done, what setup said, what is on the phone.
            putFile(zip, "import.txt", new File(ext, "logs/import.txt"));
            putFile(zip, "import-prev.txt", new File(ext, "logs/import-prev.txt"));
            putFile(zip, "setup-result.txt", new File(ext, "setup/result.txt"));
            putFile(zip, "setup-progress.txt", new File(ext, "setup/progress.txt"));
            putText(zip, "files.txt", "import：" + folderSize(new File(ext, "import")) + "\ngame：" + folderSize(new File(ext, "game"))
                + "\nsetup/original：" + folderSize(new File(ext, "setup/original")) + "\n");
            putText(zip, "exits.txt", exitHistory(context));
            File[] crashes = new File(data, "Crashes").listFiles();
            if (crashes != null)
            {
                Arrays.sort(crashes);
                for (int i = Math.max(0, crashes.length - 2); i < crashes.length; i++)
                {
                    putFile(zip, "Crashes/" + crashes[i].getName(), crashes[i]);
                }
            }
            zip.close();
            out = null;
            return where;
        }
        catch (Throwable t)
        {
            Log.e(TAG, "the report could not be written", t);
            return null;
        }
        finally
        {
            if (out != null)
            {
                try
                {
                    out.close();
                }
                catch (IOException ignored)
                {
                }
            }
        }
    }

    private static String phone(Context context, File ext)
    {
        StringBuilder text = new StringBuilder();
        text.append("手机：").append(Build.MANUFACTURER).append(' ').append(Build.MODEL).append(" (").append(Build.DEVICE).append(")\n");
        text.append("安卓：").append(Build.VERSION.RELEASE).append(" (API ").append(Build.VERSION.SDK_INT).append("), ").append(Build.DISPLAY).append('\n');
        if (Build.VERSION.SDK_INT >= 31)
        {
            text.append("芯片：").append(Build.SOC_MANUFACTURER).append(' ').append(Build.SOC_MODEL).append('\n');
        }
        text.append("硬件：").append(Build.HARDWARE).append(", ABI ").append(Arrays.toString(Build.SUPPORTED_ABIS)).append('\n');
        ActivityManager.MemoryInfo memory = new ActivityManager.MemoryInfo();
        context.getSystemService(ActivityManager.class).getMemoryInfo(memory);
        text.append("内存：").append(memory.totalMem >> 20).append(" MB，可用 ").append(memory.availMem >> 20).append(" MB\n");
        File free = ext != null ? ext : context.getFilesDir();
        text.append("存储可用：").append(free.getUsableSpace() >> 20).append(" MB\n");
        text.append("安装包：").append(readSmall(new File(ext, "payload.txt")).replace('\n', ' ')).append('\n');
        text.append("规则：").append(readSmall(new File(ext, "setup/version.txt")).replace('\n', ' ')).append('\n');
        text.append("游戏准备：").append(readSmall(new File(ext, "game/port-setup.txt")).replace('\n', ' ')).append('\n');
        text.append("启动参数：").append(readSmall(new File(ext, "game/args.txt")).replace('\n', ' ')).append('\n');
        text.append("mobile.txt：").append(readSmall(new File(ext, "game/mobile.txt")).replace('\n', ' ')).append('\n');
        return text.toString();
    }

    private static String readSmall(File file)
    {
        try
        {
            if (!file.exists())
            {
                return "（无）";
            }
            return new String(readAll(new FileInputStream(file), 64 << 10), Charset.forName("UTF-8")).trim();
        }
        catch (IOException e)
        {
            return "（读不了：" + e.getMessage() + "）";
        }
    }

    private static byte[] readAll(InputStream in, int limit) throws IOException
    {
        try
        {
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            byte[] buffer = new byte[65536];
            int n;
            while ((n = in.read(buffer)) > 0 && bytes.size() < limit)
            {
                bytes.write(buffer, 0, n);
            }
            return bytes.toByteArray();
        }
        finally
        {
            in.close();
        }
    }

    private static void putText(ZipOutputStream zip, String name, String text) throws IOException
    {
        putBytes(zip, name, SECRETS.matcher(text).replaceAll("$1***").getBytes(Charset.forName("UTF-8")));
    }

    private static void putBytes(ZipOutputStream zip, String name, byte[] bytes) throws IOException
    {
        zip.putNextEntry(new ZipEntry(name));
        zip.write(bytes);
        zip.closeEntry();
    }

    /** A text file, its last 32 MB at most, with secrets masked line by line. */
    private static void putFile(ZipOutputStream zip, String name, File file) throws IOException
    {
        if (!file.isFile())
        {
            return;
        }
        long limit = 32L << 20;
        FileInputStream in = new FileInputStream(file);
        try
        {
            long skip = Math.max(0, file.length() - limit);
            if (skip > 0)
            {
                in.skip(skip);
            }
            zip.putNextEntry(new ZipEntry(name));
            if (skip > 0)
            {
                zip.write(("（前 " + (skip >> 20) + " MB 省略）\n").getBytes(Charset.forName("UTF-8")));
            }
            ByteArrayOutputStream line = new ByteArrayOutputStream();
            byte[] buffer = new byte[65536];
            int n;
            while ((n = in.read(buffer)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    line.write(buffer[i]);
                    if (buffer[i] == '\n' || line.size() > 65536)
                    {
                        writeMasked(zip, line);
                    }
                }
            }
            writeMasked(zip, line);
            zip.closeEntry();
        }
        finally
        {
            in.close();
        }
    }

    private static void writeMasked(ZipOutputStream zip, ByteArrayOutputStream line) throws IOException
    {
        if (line.size() == 0)
        {
            return;
        }
        String text = new String(line.toByteArray(), Charset.forName("UTF-8"));
        line.reset();
        zip.write(SECRETS.matcher(text).replaceAll("$1***").getBytes(Charset.forName("UTF-8")));
    }

    // ---- the system's tombstone (protobuf, system/core/debuggerd/proto/tombstone.proto) to text ----

    static final class Tombstone
    {
        /** One field of a protobuf message: its number, and a value (varints, fixed) or the bytes (length-delimited). */
        private static final class Field
        {
            int number;
            long value;
            byte[] data;
            int offset, length;

            String text()
            {
                return new String(data, offset, length, Charset.forName("UTF-8"));
            }
        }

        private static List<Field> parse(byte[] data, int offset, int length)
        {
            List<Field> fields = new ArrayList<Field>();
            int[] pos = { offset };
            int end = offset + length;
            while (pos[0] < end)
            {
                long key = varint(data, pos);
                Field field = new Field();
                field.number = (int) (key >>> 3);
                int type = (int) (key & 7);
                if (type == 0)
                {
                    field.value = varint(data, pos);
                }
                else if (type == 1)
                {
                    long v = 0;
                    for (int i = 7; i >= 0; i--)
                    {
                        v = (v << 8) | (data[pos[0] + i] & 0xFF);
                    }
                    field.value = v;
                    pos[0] += 8;
                }
                else if (type == 2)
                {
                    int len = (int) varint(data, pos);
                    field.data = data;
                    field.offset = pos[0];
                    field.length = len;
                    pos[0] += len;
                }
                else if (type == 5)
                {
                    long v = 0;
                    for (int i = 3; i >= 0; i--)
                    {
                        v = (v << 8) | (data[pos[0] + i] & 0xFF);
                    }
                    field.value = v;
                    pos[0] += 4;
                }
                else
                {
                    break; // groups are not used in tombstones
                }
                fields.add(field);
            }
            return fields;
        }

        private static long varint(byte[] data, int[] pos)
        {
            long result = 0;
            int shift = 0;
            while (true)
            {
                int b = data[pos[0]++] & 0xFF;
                result |= (long) (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }
                shift += 7;
            }
        }

        private static List<Field> sub(Field field)
        {
            return parse(field.data, field.offset, field.length);
        }

        static String toText(byte[] tombstone)
        {
            StringBuilder text = new StringBuilder();
            List<Field> top = parse(tombstone, 0, tombstone.length);
            long crashTid = -1;
            List<Field> threads = new ArrayList<Field>();
            List<Field> buffers = new ArrayList<Field>();
            for (Field f : top)
            {
                switch (f.number)
                {
                    case 2: text.append("build: ").append(f.text()).append('\n'); break;
                    case 4: text.append("time: ").append(f.text()).append('\n'); break;
                    case 6: crashTid = f.value; break;
                    case 9: text.append("cmdline: ").append(f.text()).append('\n'); break;
                    case 10:
                    {
                        String name = "", code = "";
                        long number = 0, address = 0;
                        boolean hasAddress = false;
                        for (Field s : sub(f))
                        {
                            if (s.number == 1) number = s.value;
                            else if (s.number == 2) name = s.text();
                            else if (s.number == 4) code = s.text();
                            else if (s.number == 8) hasAddress = s.value != 0;
                            else if (s.number == 9) address = s.value;
                        }
                        text.append("signal: ").append(number).append(' ').append(name).append(" (").append(code).append(')');
                        if (hasAddress)
                        {
                            text.append(", fault address 0x").append(Long.toHexString(address));
                        }
                        text.append('\n');
                        break;
                    }
                    case 14: text.append("abort message: ").append(f.text()).append('\n'); break;
                    case 15:
                        for (Field c : sub(f))
                        {
                            if (c.number == 1) text.append("cause: ").append(c.text()).append('\n');
                        }
                        break;
                    case 16: threads.add(f); break;
                    case 18: buffers.add(f); break;
                    default: break;
                }
            }
            text.append("crashing thread: ").append(crashTid).append("\n\n");
            // The crashing thread first, then the others (name and backtrace).
            for (int pass = 0; pass < 2; pass++)
            {
                for (Field entry : threads)
                {
                    long key = -1;
                    Field thread = null;
                    for (Field e : sub(entry))
                    {
                        if (e.number == 1) key = e.value;
                        else if (e.number == 2) thread = e;
                    }
                    if (thread == null || (pass == 0) != (key == crashTid))
                    {
                        continue;
                    }
                    appendThread(text, thread, pass == 0 ? 64 : 24);
                }
            }
            for (Field buffer : buffers)
            {
                String name = "";
                List<Field> logs = new ArrayList<Field>();
                for (Field b : sub(buffer))
                {
                    if (b.number == 1) name = b.text();
                    else if (b.number == 2) logs.add(b);
                }
                text.append("\n---- log buffer ").append(name).append(" (").append(logs.size()).append(" lines) ----\n");
                for (Field log : logs)
                {
                    String time = "", tag = "", message = "";
                    long priority = 0;
                    for (Field l : sub(log))
                    {
                        if (l.number == 1) time = l.text();
                        else if (l.number == 4) priority = l.value;
                        else if (l.number == 5) tag = l.text();
                        else if (l.number == 6) message = l.text();
                    }
                    text.append(time).append(' ').append("??VDIWEF".charAt((int) Math.min(7, priority))).append(' ').append(tag).append(": ").append(message.trim()).append('\n');
                }
            }
            return text.toString();
        }

        private static void appendThread(StringBuilder text, Field thread, int maxFrames)
        {
            String name = "";
            long id = 0;
            List<Field> frames = new ArrayList<Field>();
            for (Field t : sub(thread))
            {
                if (t.number == 1) id = t.value;
                else if (t.number == 2) name = t.text();
                else if (t.number == 4) frames.add(t);
                else if (t.number == 7) text.append("  note: ").append(t.text()).append('\n');
            }
            text.append("thread ").append(id).append(" \"").append(name).append("\"\n");
            int n = 0;
            for (Field frame : frames)
            {
                if (n++ >= maxFrames)
                {
                    text.append("  ... ").append(frames.size() - maxFrames).append(" more\n");
                    break;
                }
                long relPc = 0, offset = 0;
                String function = "", file = "";
                for (Field f : sub(frame))
                {
                    if (f.number == 1) relPc = f.value;
                    else if (f.number == 4) function = f.text();
                    else if (f.number == 5) offset = f.value;
                    else if (f.number == 6) file = f.text();
                }
                text.append(String.format(Locale.US, "  #%02d pc %012x  %s", n - 1, relPc, file));
                if (!function.isEmpty())
                {
                    text.append(" (").append(function).append('+').append(offset).append(')');
                }
                text.append('\n');
            }
            text.append('\n');
        }
    }
}

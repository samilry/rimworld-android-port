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
import java.util.Enumeration;
import java.util.List;
import java.util.Locale;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
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

    /**
     * An exception no Java code caught is written to logs/java-crash.txt (with the import's trail) before the process
     * ends as it would: the system keeps no trace of a Java crash for us, and some phones (a tester's vivo) let an app
     * read nothing of logcat.
     */
    static void recordJavaCrashes(final Context context)
    {
        final Thread.UncaughtExceptionHandler before = Thread.getDefaultUncaughtExceptionHandler();
        final Context app = context.getApplicationContext() != null ? context.getApplicationContext() : context;
        Thread.setDefaultUncaughtExceptionHandler(new Thread.UncaughtExceptionHandler()
        {
            @Override
            public void uncaughtException(Thread thread, Throwable problem)
            {
                try
                {
                    java.io.StringWriter text = new java.io.StringWriter();
                    problem.printStackTrace(new java.io.PrintWriter(text));
                    File dir = new File(app.getExternalFilesDir(null), "logs");
                    dir.mkdirs();
                    FileOutputStream out = new FileOutputStream(new File(dir, "java-crash.txt"), true);
                    try
                    {
                        String stamp = new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(new Date());
                        out.write((stamp + " [" + android.os.Process.myPid() + "] thread " + thread.getName() + "\n" + text + "\n").getBytes("UTF-8"));
                    }
                    finally
                    {
                        out.close();
                    }
                    trail(app, "Java crash on thread " + thread.getName() + ": " + problem);
                }
                catch (Throwable ignored)
                {
                }
                if (before != null)
                {
                    before.uncaughtException(thread, problem);
                }
            }
        });
    }

    /**
     * For the crash dialog: when the imported game is newer than the version the port was made for, the likely reason
     * (a tester's crash at start, 2026-10-27: the port of 10-25 on 1.7.4990). Empty otherwise.
     */
    static String olderThanGame(Context context)
    {
        File ext = context.getExternalFilesDir(null);
        String[] rules = MainActivity.readText(new File(ext, "setup/version.txt")).trim().split("\\s+");
        String[] game = MainActivity.readText(new File(ext, "game/Version.txt")).trim().split("\\s+");
        String builtFor = rules.length > 1 ? rules[1] : "", version = game.length > 0 ? game[0] : "";
        if (MainActivity.buildOf(builtFor) > 0 && MainActivity.buildOf(version) > MainActivity.buildOf(builtFor))
        {
            return "很可能是因为安装包比游戏旧：游戏是 " + version + "，安装包是为 " + builtFor + " 做的。请先下载最新的安装包覆盖安装。\n\n";
        }
        return "";
    }

    /** From MainActivity.onCreate: the shortcut, and a report when the previous run ended badly. */
    static void onStart(final Activity activity)
    {
        recordJavaCrashes(activity);
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
                                .setMessage(firstLine(found) + "\n\n" + olderThanGame(activity)
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
            java.util.List<ShortcutInfo> list = new java.util.ArrayList<ShortcutInfo>();
            list.add(info);
            // The tester's APK (it carries the setup): the game can be taken again from another copy (ReimportActivity).
            File ext = context.getExternalFilesDir(null);
            if (ext != null && new File(ext, "setup/PortSetup.dll").exists())
            {
                list.add(new ShortcutInfo.Builder(context, "reimport-game")
                    .setShortLabel("重新导入游戏")
                    .setLongLabel("重新导入游戏（换游戏版本）")
                    .setIntent(new Intent(Intent.ACTION_VIEW).setClassName(context.getPackageName(), ReimportActivity.class.getName()))
                    .build());
            }
            shortcuts.setDynamicShortcuts(list);
        }
        catch (Throwable t)
        {
            Log.w(TAG, "no export shortcut: " + t);
        }
        // The game's folders in the system's Files app (FilesProvider): most makers' launchers leave that app out, and a
        // file chooser only picks, it does not copy. A call of its own: a launcher that refuses it keeps the ones above.
        try
        {
            Intent files = FilesProvider.placesIntent(context);
            if (files != null)
            {
                context.getSystemService(ShortcutManager.class).addDynamicShortcuts(java.util.Collections.singletonList(new ShortcutInfo.Builder(context, "game-files")
                    .setShortLabel("存档和 Mod")
                    .setLongLabel("在「文件」里打开存档、设置和 Mod")
                    .setIntent(files)
                    .build()));
            }
        }
        catch (Throwable t)
        {
            Log.w(TAG, "no files shortcut: " + t);
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

    /** Whether logs/import.txt last saw the process (pid) leave the screen or its window lose the focus (the recent apps). */
    private static boolean leftTheScreen(Context context, int pid)
    {
        String tag = "[" + pid + "] ";
        boolean away = false;
        File dir = new File(context.getExternalFilesDir(null), "logs");
        for (String name : new String[] { "import-prev.txt", "import.txt" })
        {
            try
            {
                File file = new File(dir, name);
                if (!file.isFile())
                {
                    continue;
                }
                for (String line : new String(readAll(new FileInputStream(file), 4 << 20), Charset.forName("UTF-8")).split("\n"))
                {
                    if (line.contains(tag))
                    {
                        if (line.contains("the app leaves the screen") || line.contains("swiped away") || line.contains("the window lost the focus"))
                        {
                            away = true;
                        }
                        else if (line.contains("the app is on the screen") || line.contains("the window has the focus") || line.contains("] started"))
                        {
                            away = false;
                        }
                    }
                }
            }
            catch (Throwable ignored)
            {
            }
        }
        return away;
    }

    private static boolean abnormal(Context context, ApplicationExitInfo exit)
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
                // Killed while on the screen: not something the player chose. (In the background the system ends apps all the
                // time.) The importance alone does not tell: with the game's foreground service it is 125 off the screen too,
                // and a tester's OPPO reported every swipe-away from the recent apps ("o-stop(40)", a second after the app
                // left the screen) as a crash. The trail says whether the process had left the screen.
                return exit.getImportance() <= ActivityManager.RunningAppProcessInfo.IMPORTANCE_VISIBLE && !leftTheScreen(context, exit.getPid());
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
        // The last few are kept: they can come close together (a tester's Redmi, 2026-10-22: after the game was swiped
        // away the system started the app twice more, 12 s apart, and each ended itself; with only the newer one
        // remembered, the first came back as a crash report).
        SharedPreferences prefs = context.getSharedPreferences("diagnostics", Context.MODE_PRIVATE);
        StringBuilder kept = new StringBuilder().append(System.currentTimeMillis());
        String[] earlier = prefs.getString("selfExits", "").split(",");
        for (int i = 0; i < earlier.length && i < 7; i++)
        {
            if (!earlier[i].isEmpty())
            {
                kept.append(',').append(earlier[i]);
            }
        }
        prefs.edit().putString("selfExits", kept.toString()).commit();
    }

    /** Whether an exit is one the app announced (noteSelfExit): within a few seconds after one of them. */
    private static boolean announced(ApplicationExitInfo exit, SharedPreferences prefs)
    {
        String notes = prefs.getString("selfExits", "") + "," + prefs.getLong("selfExitAt", 0);
        for (String note : notes.split(","))
        {
            try
            {
                long selfExitAt = Long.parseLong(note.trim());
                long after = exit.getTimestamp() - selfExitAt;
                if (selfExitAt > 0 && after >= -1000 && after <= 15000)
                {
                    return true;
                }
            }
            catch (NumberFormatException ignored)
            {
            }
        }
        return false;
    }

    /**
     * The system log as far as this app may read it (its own processes': the host, the runtime, the setup process, the
     * interposer, what the system said about them), the newest 6000 lines.
     */
    private static String ownLogcat()
    {
        try
        {
            Process logcat = Runtime.getRuntime().exec(new String[] { "logcat", "-d", "-v", "threadtime", "-t", "6000" });
            byte[] text = readAll(logcat.getInputStream(), 8 << 20);
            logcat.destroy();
            return new String(text, Charset.forName("UTF-8"));
        }
        catch (Throwable t)
        {
            return "logcat 读不了：" + t + "\n";
        }
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
        if (seen == 0)
        {
            // The first start with this data: a new install, or the app's storage was cleared, and with it the notes of
            // the exits the app announced (a tester's Xiaomi, 2026-10-22: a restart after a change of mods, from before the
            // storage was cleared, came back as a crash report). What happened before is not this data's to report.
            seen = System.currentTimeMillis();
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
            if (exit.getTimestamp() > seen && abnormal(context, exit) && !announced(exit, prefs))
            {
                // A native crash and the Java crash it brings can be recorded in the same second (a tester's Xiaomi): the
                // native one is reported, its tombstone is where the crash is.
                boolean sameTime = newest != null && Math.abs(exit.getTimestamp() - newest.getTimestamp()) < 3000;
                boolean nativeOne = exit.getReason() == ApplicationExitInfo.REASON_CRASH_NATIVE;
                if (newest == null
                    || (sameTime ? nativeOne && newest.getReason() != ApplicationExitInfo.REASON_CRASH_NATIVE : exit.getTimestamp() > newest.getTimestamp()))
                {
                    newest = exit;
                }
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
                Tombstone.methodMaps = methodMaps(context);
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
            // First to read: the logs below in a page (ReportSummary), and every mod folder with its place in the load order.
            putText(zip, "summary.txt", ReportSummary.summary(ext, logs));
            putText(zip, "mods.txt", ReportSummary.mods(new File(ext, "game"), new File(data, "Config/ModsConfig.xml")));
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
            putFile(zip, "stderr-1.txt", new File(ext, "logs/stderr-1.txt"));
            putFile(zip, "ModsConfig.xml", new File(data, "Config/ModsConfig.xml"));
            // The import of the player's game (the tester's APK): what was done, what setup said, what is on the phone.
            putFile(zip, "import.txt", new File(ext, "logs/import.txt"));
            putFile(zip, "import-prev.txt", new File(ext, "logs/import-prev.txt"));
            putFile(zip, "setup-result.txt", new File(ext, "setup/result.txt"));
            putFile(zip, "setup-progress.txt", new File(ext, "setup/progress.txt"));
            putText(zip, "files.txt", "import：" + folderSize(new File(ext, "import")) + "\ngame：" + folderSize(new File(ext, "game"))
                + "\nsetup/original：" + folderSize(new File(ext, "setup/original")) + "\n");
            putText(zip, "exits.txt", exitHistory(context));
            putFile(zip, "setup-stderr.txt", new File(ext, "logs/setup-stderr.txt"));
            putFile(zip, "java-crash.txt", new File(ext, "logs/java-crash.txt"));
            putFile(zip, "setup-init-error.txt", new File(ext, "setup/init-error.txt"));
            putText(zip, "logcat.txt", ownLogcat());
            // The run's record (PortMobile's Recorder: what the game was doing, every few seconds and at every event) of
            // this run and the two before, and the newest three moments the player marked with "记录问题" (a screenshot each).
            // ...and the watch (PortHooks' Watch): memory and the game's last log line while it loads, and the stalls.
            for (String file : new String[] { "record.txt", "record-1.txt", "record-2.txt", "watch.txt", "watch-1.txt" })
            {
                putFile(zip, file, new File(ext, "logs/" + file));
            }
            File[] marks = new File(ext, "logs/marks").listFiles();
            if (marks != null)
            {
                Arrays.sort(marks);
                int taken = 0;
                for (int i = marks.length - 1; i >= 0 && taken < 3; i--)
                {
                    String mark = marks[i].getName();
                    if (mark.endsWith(".txt"))
                    {
                        String stem = mark.substring(0, mark.length() - 4);
                        putFile(zip, "marks/" + mark, marks[i]);
                        putBinary(zip, "marks/" + stem + ".png", new File(marks[i].getParentFile(), stem + ".png"));
                        // The pictures of the selected things the mark took (stem-N-atlas-L.png, stem-N-texture-L.png).
                        for (File picture : marks)
                        {
                            if (picture.getName().startsWith(stem + "-") && picture.getName().endsWith(".png"))
                            {
                                putBinary(zip, "marks/" + picture.getName(), picture);
                            }
                        }
                        taken++;
                    }
                }
            }
            putFile(zip, "mobile.txt", new File(ext, "game/mobile.txt"));
            putFile(zip, "Prefs.xml", new File(data, "Config/Prefs.xml"));
            File[] crashes = new File(data, "Crashes").listFiles();
            if (crashes != null)
            {
                Arrays.sort(crashes);
                for (int i = Math.max(0, crashes.length - 2); i < crashes.length; i++)
                {
                    putFile(zip, "Crashes/" + crashes[i].getName(), crashes[i]);
                }
            }
            putDesyncReports(zip, data);
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
        text.append("不保留活动：").append(activitiesNotKept(context) ? "开（开发者选项）" : "关").append('\n');
        return text.toString();
    }

    /**
     * The developer option "不保留活动" (Don't keep activities): the system destroys every activity as soon as it leaves
     * the screen. The game's activity cannot outlive that (a NativeActivity; a new one would start the game a second time
     * in the process), so the game ends when another app or the file chooser comes up.
     */
    static boolean activitiesNotKept(Context context)
    {
        try
        {
            return android.provider.Settings.Global.getInt(context.getContentResolver(), android.provider.Settings.Global.ALWAYS_FINISH_ACTIVITIES, 0) != 0;
        }
        catch (Throwable ignored)
        {
            return false;
        }
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
        // A file over 32 MB keeps its first 8 MB and its last 24: how a start went (the start of the game's log) and how it
        // ended. (Until 2026-10-27 only the end: a log that one error a frame had grown to 372 MB lost how its start went.)
        long limit = 32L << 20, head = 8L << 20;
        long length = file.length();
        long skip = Math.max(0, length - limit);
        FileInputStream in = new FileInputStream(file);
        try
        {
            zip.putNextEntry(new ZipEntry(name));
            ByteArrayOutputStream line = new ByteArrayOutputStream();
            if (skip > 0)
            {
                copyMasked(zip, in, head, line);
                writeMasked(zip, line);
                long left = length - head - (limit - head);
                long skipped = 0;
                while (skipped < left)
                {
                    long n = in.skip(left - skipped);
                    if (n <= 0)
                    {
                        break;
                    }
                    skipped += n;
                }
                zip.write(("\n（中间 " + (skipped >> 20) + " MB 省略）\n").getBytes(Charset.forName("UTF-8")));
            }
            copyMasked(zip, in, Long.MAX_VALUE, line);
            writeMasked(zip, line);
            zip.closeEntry();
        }
        finally
        {
            in.close();
        }
    }

    /** Up to count bytes of in, line by line through writeMasked (a line left over stays in line). */
    private static void copyMasked(ZipOutputStream zip, FileInputStream in, long count, ByteArrayOutputStream line) throws IOException
    {
        byte[] buffer = new byte[65536];
        int n;
        while (count > 0 && (n = in.read(buffer, 0, (int)Math.min(buffer.length, count))) > 0)
        {
            count -= n;
            for (int i = 0; i < n; i++)
            {
                line.write(buffer[i]);
                if (buffer[i] == '\n' || line.size() > 65536)
                {
                    writeMasked(zip, line);
                }
            }
        }
    }

    /**
     * The Multiplayer mod's desync reports (MpDesyncs, and the guests' that the host keeps in MpDesyncs/Guests): the newest
     * two of each, without the saved games in them (the large parts; the traces and the info say where the sides parted).
     * A tester's "联机不稳定" (2026-10-21) had one in Player.log and nothing else to tell the cause.
     */
    private static void putDesyncReports(ZipOutputStream zip, File data)
    {
        for (String folder : new String[] { "MpDesyncs", "MpDesyncs/Guests" })
        {
            File[] reports = new File(data, folder).listFiles();
            if (reports == null)
            {
                continue;
            }
            Arrays.sort(reports, new java.util.Comparator<File>()
            {
                @Override
                public int compare(File a, File b)
                {
                    return Long.compare(a.lastModified(), b.lastModified());
                }
            });
            int taken = 0;
            for (int i = reports.length - 1; i >= 0 && taken < 2; i--)
            {
                File report = reports[i];
                String into = folder + "/" + report.getName();
                try
                {
                    if (report.isDirectory())
                    {
                        File[] parts = report.listFiles();
                        for (File part : parts != null ? parts : new File[0])
                        {
                            if (part.isFile() && part.length() <= DESYNC_PART_LIMIT)
                            {
                                putBinary(zip, into + "/" + part.getName(), part);
                            }
                        }
                        taken++;
                    }
                    else if (report.getName().endsWith(".zip"))
                    {
                        try (ZipFile reportZip = new ZipFile(report))
                        {
                            Enumeration<? extends ZipEntry> entries = reportZip.entries();
                            while (entries.hasMoreElements())
                            {
                                ZipEntry entry = entries.nextElement();
                                if (entry.isDirectory() || entry.getSize() < 0 || entry.getSize() > DESYNC_PART_LIMIT)
                                {
                                    continue;
                                }
                                zip.putNextEntry(new ZipEntry(into + "/" + entry.getName()));
                                try (InputStream in = reportZip.getInputStream(entry))
                                {
                                    byte[] buffer = new byte[65536];
                                    int n;
                                    while ((n = in.read(buffer)) > 0)
                                    {
                                        zip.write(buffer, 0, n);
                                    }
                                }
                                zip.closeEntry();
                            }
                        }
                        taken++;
                    }
                }
                catch (Throwable problem)
                {
                    Log.w(TAG, "desync report " + report + " left out", problem);
                }
            }
        }
    }

    private static final long DESYNC_PART_LIMIT = 4L << 20;

    /** A file as it is (a screenshot), when it is there. */
    private static void putBinary(ZipOutputStream zip, String name, File file) throws IOException
    {
        if (!file.isFile())
        {
            return;
        }
        FileInputStream in = new FileInputStream(file);
        try
        {
            zip.putNextEntry(new ZipEntry(name));
            byte[] buffer = new byte[65536];
            int n;
            while ((n = in.read(buffer)) > 0)
            {
                zip.write(buffer, 0, n);
            }
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

    // ---- the runtime's list of compiled methods, after a native crash ----

    /** Where the runtime writes perf-<pid>.map while methodMapWanted says so (host.c). */
    static File methodMaps(Context context)
    {
        return new File(context.getFilesDir(), "perfmap");
    }

    /**
     * From the host (MainActivity.methodMapWanted) before the runtime starts: whether it writes its list of compiled
     * methods this run. On for three days from a native crash not seen here before: a crash in compiled managed code has
     * only an address in its tombstone, and the report of the next one names the method from that run's list
     * (Tombstone.appendFrames). The list costs about 3 % of the start, so it is off otherwise. The newest list, the
     * crashed run's that the report being made now reads (checkLastExit, on its own thread), is kept; older ones go.
     */
    static boolean methodMapWanted(Context context)
    {
        SharedPreferences prefs = context.getSharedPreferences("diagnostics", Context.MODE_PRIVATE);
        long crash = prefs.getLong("methodMapCrash", 0), until = prefs.getLong("methodMapUntil", 0);
        if (Build.VERSION.SDK_INT >= 30)
        {
            try
            {
                ActivityManager manager = context.getSystemService(ActivityManager.class);
                for (ApplicationExitInfo exit : manager.getHistoricalProcessExitReasons(context.getPackageName(), 0, 8))
                {
                    if (context.getPackageName().equals(exit.getProcessName()) && exit.getReason() == ApplicationExitInfo.REASON_CRASH_NATIVE && exit.getTimestamp() > crash)
                    {
                        crash = exit.getTimestamp();
                        until = Math.max(until, exit.getTimestamp() + 3L * 24 * 3600 * 1000);
                    }
                }
            }
            catch (Throwable t)
            {
                Log.e(TAG, "exit reasons for the method list", t);
            }
        }
        prefs.edit().putLong("methodMapCrash", crash).putLong("methodMapUntil", until).apply();
        boolean on = System.currentTimeMillis() < until;
        File[] maps = methodMaps(context).listFiles(new java.io.FilenameFilter()
        {
            @Override
            public boolean accept(File dir, String name)
            {
                return name.startsWith("perf-") && name.endsWith(".map");
            }
        });
        if (maps != null)
        {
            Arrays.sort(maps, new java.util.Comparator<File>()
            {
                @Override
                public int compare(File a, File b)
                {
                    return Long.compare(b.lastModified(), a.lastModified());
                }
            });
            for (int i = 1; i < maps.length; i++)
            {
                maps[i].delete();
            }
        }
        trail(context, on ? "the runtime lists its compiled methods this run (a native crash on " + new SimpleDateFormat("MM-dd HH:mm", Locale.US).format(new Date(crash)) + ")" : "no method list this run");
        return on;
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

        /** Where the runtime's lists of compiled methods are (Diagnostics.methodMaps), set before toText. */
        static File methodMaps;

        /** The crashed run's list of compiled methods, if it wrote one: start, size and the place of each line in the file. */
        private static final class MethodMap
        {
            File file;
            int count;
            long[] starts = new long[1 << 15];
            int[] sizes = new int[1 << 15];
            long[] lines = new long[1 << 15];

            /** Lines "0x<start> <size in hex> <name>", in the order the runtime compiled the methods. */
            static MethodMap read(File file) throws IOException
            {
                MethodMap map = new MethodMap();
                map.file = file;
                InputStream in = new java.io.BufferedInputStream(new FileInputStream(file), 1 << 16);
                try
                {
                    long offset = 0, lineStart = 0, start = 0, size = 0;
                    int field = 0, b;
                    boolean hex = false;
                    while ((b = in.read()) >= 0)
                    {
                        offset++;
                        if (b == '\n')
                        {
                            if (field >= 2)
                            {
                                map.add(start, (int) size, lineStart);
                            }
                            lineStart = offset;
                            start = size = 0;
                            field = 0;
                            hex = false;
                            continue;
                        }
                        if (field >= 2)
                        {
                            continue;
                        }
                        if (b == ' ')
                        {
                            field++;
                            hex = false;
                            continue;
                        }
                        if (field == 0 && (b == 'x' || b == 'X'))
                        {
                            hex = true;
                            start = 0;
                            continue;
                        }
                        int digit = Character.digit(b, 16);
                        if (digit < 0)
                        {
                            continue;
                        }
                        if (field == 0 && hex)
                        {
                            start = (start << 4) | digit;
                        }
                        else if (field == 1)
                        {
                            size = (size << 4) | digit;
                        }
                    }
                }
                finally
                {
                    in.close();
                }
                return map;
            }

            private void add(long start, int size, long line)
            {
                if (count == starts.length)
                {
                    starts = Arrays.copyOf(starts, count * 2);
                    sizes = Arrays.copyOf(sizes, count * 2);
                    lines = Arrays.copyOf(lines, count * 2);
                }
                starts[count] = start;
                sizes[count] = size;
                lines[count] = line;
                count++;
            }

            /** The method whose code holds the address, the latest compiled where code was used again; null for none. */
            String at(long pc)
            {
                int found = -1;
                for (int i = 0; i < count; i++)
                {
                    if (pc >= starts[i] && pc < starts[i] + sizes[i])
                    {
                        found = i;
                    }
                }
                if (found < 0)
                {
                    return null;
                }
                try
                {
                    java.io.RandomAccessFile raf = new java.io.RandomAccessFile(file, "r");
                    try
                    {
                        raf.seek(lines[found]);
                        ByteArrayOutputStream line = new ByteArrayOutputStream();
                        int b;
                        while ((b = raf.read()) >= 0 && b != '\n')
                        {
                            line.write(b);
                        }
                        String text = new String(line.toByteArray(), Charset.forName("UTF-8"));
                        int name = text.indexOf(' ', text.indexOf(' ') + 1);
                        return text.substring(name + 1) + String.format(Locale.US, " +0x%x", pc - starts[found]);
                    }
                    finally
                    {
                        raf.close();
                    }
                }
                catch (IOException e)
                {
                    return null;
                }
            }
        }

        private static MethodMap map;

        static String toText(byte[] tombstone)
        {
            StringBuilder text = new StringBuilder();
            List<Field> top = parse(tombstone, 0, tombstone.length);
            long crashTid = -1, faultAddress = 0, abortAddress = 0, pid = -1;
            boolean hasFault = false;
            List<Field> threads = new ArrayList<Field>();
            List<Field> buffers = new ArrayList<Field>();
            mappings = new ArrayList<Field>();
            libraries = new java.util.TreeMap<String, String>();
            for (Field f : top)
            {
                switch (f.number)
                {
                    case 2: text.append("build: ").append(f.text()).append('\n'); break;
                    case 4: text.append("time: ").append(f.text()).append('\n'); break;
                    case 5: pid = f.value; break;
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
                            hasFault = true;
                            faultAddress = address;
                        }
                        text.append('\n');
                        break;
                    }
                    case 14:
                    {
                        text.append("abort message: ").append(f.text()).append('\n');
                        // Scudo's "corrupted chunk header at address 0x2000071878b0c50": the top byte is the pointer's tag.
                        java.util.regex.Matcher at = Pattern.compile("address 0x([0-9a-fA-F]+)").matcher(f.text());
                        if (at.find())
                        {
                            abortAddress = new java.math.BigInteger(at.group(1), 16).longValue() & 0x00ffffffffffffffL;
                        }
                        break;
                    }
                    case 15:
                        for (Field c : sub(f))
                        {
                            if (c.number == 1) text.append("cause: ").append(c.text()).append('\n');
                            else if (c.number == 2) appendMemoryError(text, c);
                        }
                        break;
                    case 16: threads.add(f); break;
                    case 17: mappings.add(f); break;
                    case 18: buffers.add(f); break;
                    default: break;
                }
            }
            if (hasFault)
            {
                // What the address belongs to: a library, the heap (scudo), a GWP-ASan guard page, a thread's stack, JIT
                // code, or nothing (a wild pointer).
                text.append("fault address in: ").append(mappingOf(faultAddress)).append('\n');
            }
            if (abortAddress != 0)
            {
                text.append(String.format(Locale.US, "abort message's address 0x%x in: ", abortAddress)).append(mappingOf(abortAddress)).append('\n');
            }
            // Its stack's size: a stack overflow (EEPolicy::HandleFatalStackOverflow) on a thread whose stack is smaller
            // than the PC's 1.5 MB is the port's (two of 2026-10-28: the loading thread with bionic's 1 MB).
            text.append("crashing thread: ").append(crashTid).append(", its stack ").append(stackOf(crashTid)).append('\n');
            // The runtime's list of the methods it compiled in the crashed run (host.c, for some days after a native crash).
            map = null;
            File list = methodMaps == null || pid < 0 ? null : new File(methodMaps, "perf-" + pid + ".map");
            if (list != null && list.isFile())
            {
                try
                {
                    map = MethodMap.read(list);
                    text.append("compiled methods: ").append(map.count).append(" listed by the runtime (").append(list.getName()).append(")\n\n");
                }
                catch (IOException e)
                {
                    text.append("compiled methods: ").append(list.getName()).append(" not read: ").append(e.getMessage()).append("\n\n");
                }
            }
            else
            {
                text.append("compiled methods: not listed in this run (the runtime lists them for three days after a native crash, from the next start on)\n\n");
            }
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
            if (!libraries.isEmpty())
            {
                // For host/symbolize.sh: the build id of each library in the backtraces finds its symbols.
                text.append("libraries in the backtraces (build id):\n");
                for (java.util.Map.Entry<String, String> library : libraries.entrySet())
                {
                    text.append("  ").append(library.getKey()).append("  ").append(library.getValue()).append('\n');
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

        /**
         * A memory error the system's allocator found (GWP-ASan, which the manifest switches on; MTE): the kind, the
         * heap object, and where it was allocated and freed (tombstone.proto MemoryError / HeapObject). These name the
         * code that wrote where it should not, which the crashing thread's own backtrace (often a later free) does not.
         */
        private static void appendMemoryError(StringBuilder text, Field error)
        {
            String[] tools = { "GWP-ASan", "Scudo" };
            String[] types = { "unknown", "use after free", "double free", "invalid free", "buffer overflow", "buffer underflow" };
            long tool = 0, type = 0;
            Field heap = null;
            for (Field e : sub(error))
            {
                if (e.number == 1) tool = e.value;
                else if (e.number == 2) type = e.value;
                else if (e.number == 3) heap = e;
            }
            text.append("memory error: ").append(tool < tools.length ? tools[(int) tool] : "tool " + tool).append(", ")
                .append(type < types.length ? types[(int) type] : "type " + type).append('\n');
            if (heap == null)
            {
                return;
            }
            long address = 0, size = 0, allocTid = 0, freeTid = 0;
            List<Field> alloc = new ArrayList<Field>(), free = new ArrayList<Field>();
            for (Field h : sub(heap))
            {
                if (h.number == 1) address = h.value;
                else if (h.number == 2) size = h.value;
                else if (h.number == 3) allocTid = h.value;
                else if (h.number == 4) alloc.add(h);
                else if (h.number == 5) freeTid = h.value;
                else if (h.number == 6) free.add(h);
            }
            text.append(String.format(Locale.US, "heap object 0x%x, %d bytes\n", address, size));
            text.append("allocated by thread ").append(allocTid).append(":\n");
            appendFrames(text, alloc, 32);
            if (!free.isEmpty())
            {
                text.append("freed by thread ").append(freeTid).append(":\n");
                appendFrames(text, free, 32);
            }
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
            appendFrames(text, frames, maxFrames);
        }

        /** The process's memory mappings (tombstone.proto MemoryMapping) and the libraries the frames named, of the tombstone being read. */
        private static List<Field> mappings = new ArrayList<Field>();
        private static java.util.Map<String, String> libraries = new java.util.TreeMap<String, String>();

        /** "name +0xoffset (r-x)" of the mapping that holds an address, or that none does. */
        private static String mappingOf(long address)
        {
            for (Field mapping : mappings)
            {
                long begin = 0, end = 0, offset = 0;
                boolean read = false, write = false, execute = false;
                String name = "";
                for (Field m : sub(mapping))
                {
                    if (m.number == 1) begin = m.value;
                    else if (m.number == 2) end = m.value;
                    else if (m.number == 3) offset = m.value;
                    else if (m.number == 4) read = m.value != 0;
                    else if (m.number == 5) write = m.value != 0;
                    else if (m.number == 6) execute = m.value != 0;
                    else if (m.number == 7) name = m.text();
                }
                if (Long.compareUnsigned(address, begin) >= 0 && Long.compareUnsigned(address, end) < 0)
                {
                    return (name.isEmpty() ? "(anonymous)" : name) + String.format(Locale.US, " +0x%x (%c%c%c)", address - begin + offset, read ? 'r' : '-', write ? 'w' : '-', execute ? 'x' : '-');
                }
            }
            return mappings.isEmpty() ? "(no mappings in the tombstone)" : "no mapping (a pointer to nowhere)";
        }

        /** "1008 KB": the writable part of a thread's "[anon:stack_and_tls:TID]" mapping (bionic's guard page beside it is not). */
        private static String stackOf(long tid)
        {
            String want = "[anon:stack_and_tls:" + tid + "]";
            long bytes = 0;
            for (Field mapping : mappings)
            {
                long begin = 0, end = 0;
                boolean write = false;
                String name = "";
                for (Field m : sub(mapping))
                {
                    if (m.number == 1) begin = m.value;
                    else if (m.number == 2) end = m.value;
                    else if (m.number == 5) write = m.value != 0;
                    else if (m.number == 7) name = m.text();
                }
                if (write && name.equals(want)) bytes += end - begin;
            }
            return bytes == 0 ? "not among the mappings" : (bytes >> 10) + " KB";
        }

        private static void appendFrames(StringBuilder text, List<Field> frames, int maxFrames)
        {
            int n = 0;
            for (Field frame : frames)
            {
                if (n++ >= maxFrames)
                {
                    text.append("  ... ").append(frames.size() - maxFrames).append(" more\n");
                    break;
                }
                long relPc = 0, pc = 0, offset = 0;
                String function = "", file = "", buildId = "";
                for (Field f : sub(frame))
                {
                    if (f.number == 1) relPc = f.value;
                    else if (f.number == 2) pc = f.value;
                    else if (f.number == 4) function = f.text();
                    else if (f.number == 5) offset = f.value;
                    else if (f.number == 6) file = f.text();
                    else if (f.number == 8) buildId = f.text();
                }
                text.append(String.format(Locale.US, "  #%02d pc %012x  %s", n - 1, relPc, file));
                if (!function.isEmpty())
                {
                    text.append(" (").append(function).append('+').append(offset).append(')');
                }
                else if (file.isEmpty() || file.startsWith("<anonymous") || file.startsWith("[anon:") || file.contains("doublemapper"))
                {
                    // The runtime's compiled code (CoreCLR maps it through memfd "doublemapper"): a managed method,
                    // named from the crashed run's list where there is one (a Harmony replacement is
                    // "dynamicClass::Type.Method_PatchN"), else only in the runtime's own report in stderr.txt if any.
                    String method = map != null && pc != 0 ? map.at(pc) : null;
                    text.append(method != null ? " (JIT code: " + method + ")" : " (JIT code, a managed method)");
                }
                if (!buildId.isEmpty() && file.endsWith(".so"))
                {
                    libraries.put(file.substring(file.lastIndexOf('/') + 1), buildId);
                }
                text.append('\n');
            }
            text.append('\n');
        }
    }
}

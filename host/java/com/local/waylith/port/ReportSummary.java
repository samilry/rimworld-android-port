package com.local.waylith.port;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.nio.charset.Charset;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Two files of the report (Diagnostics.export) that are read first: summary.txt, what the logs say in a page (the
 * game's errors grouped and counted, the first of them, the record's conflicts, slow frames and gaps, the watch's
 * stalls and the load's last memory), and mods.txt, every mod folder on the phone with its id, versions and place in
 * the load order. A tester's Player.log held 10670 errors of 326 mods: which of them came first and which repeat is
 * what a report needs, and the logs are still in the zip as they are.
 */
final class ReportSummary
{
    private static final Charset UTF8 = Charset.forName("UTF-8");
    /** "2026-10-27 08:03:40.798 ERROR [Verse] text": the engine's log lines (Player.log). */
    private static final Pattern LOG_LINE = Pattern.compile("^(\\d{4}-\\d\\d-\\d\\d \\d\\d:\\d\\d:\\d\\d)\\.\\d+ +([A-Z]+) \\[([^\\]]*)\\] ?(.*)$");
    private static final Pattern NUMBERS = Pattern.compile("0x[0-9a-fA-F]+|\\d+");
    /** Frames of the logging itself, not where an error came from. */
    private static final String[] PLUMBING = { "at Brutal.Logging.", "at Verse.Log.", "at Verse.Log+", "at System.Environment", "at UnityEngine.Debug", "at Verse.Log:" };

    private ReportSummary()
    {
    }

    private static final class Group
    {
        String message, frame, first, last;
        int count, order;
    }

    static String summary(File ext, File logs)
    {
        StringBuilder text = new StringBuilder();
        text.append("报告摘要：先看这里，详细内容在同一压缩包的各个文件里。\n");
        try
        {
            portLog(text, new File(logs, "Port17.log"), "Port17.log（这次运行）");
            playerLog(text, new File(logs, "Player.log"), "Player.log（这次运行）");
            playerLog(text, new File(logs, "Player-prev.log"), "Player-prev.log（上次运行）");
            record(text, new File(ext, "logs/record.txt"), "record.txt（这次运行的记录）");
            record(text, new File(ext, "logs/record-1.txt"), "record-1.txt（上次运行的记录）");
            watch(text, new File(ext, "logs/watch.txt"), "watch.txt（这次运行：加载和卡住时的内存）");
            watch(text, new File(ext, "logs/watch-1.txt"), "watch-1.txt（上次运行）");
            runtimeLastWords(text, new File(ext, "logs/stderr-1.txt"), "stderr-1.txt（上次运行：运行时结束进程前的话）");
            runtimeLastWords(text, new File(ext, "logs/stderr.txt"), "stderr.txt（这次运行）");
        }
        catch (Throwable t)
        {
            text.append("\n（摘要没写完：").append(t).append("）\n");
        }
        return text.toString();
    }

    private static void playerLog(StringBuilder text, File file, String title) throws IOException
    {
        text.append("\n== ").append(title).append(" ==\n");
        if (!file.isFile())
        {
            text.append("（无）\n");
            return;
        }
        Map<String, Group> groups = new HashMap<String, Group>();
        Map<String, Integer> levels = new LinkedHashMap<String, Integer>();
        String firstTime = null, lastTime = null, version = null;
        Group open = null;
        String openKey = null;
        int lines = 0;
        BufferedReader in = new BufferedReader(new InputStreamReader(new FileInputStream(file), UTF8), 1 << 16);
        try
        {
            String line;
            while ((line = in.readLine()) != null)
            {
                lines++;
                Matcher m = LOG_LINE.matcher(line);
                if (!m.matches())
                {
                    // The stack under an error: the first frame that is not the logging names where it came from.
                    String trimmed = line.trim();
                    if (open != null && open.frame == null && trimmed.startsWith("at ") && !isPlumbing(trimmed))
                    {
                        open.frame = cut(trimmed.replaceAll(" \\[0x[0-9A-Fa-f]+\\]$", ""), 200);
                    }
                    continue;
                }
                String time = m.group(1), level = m.group(2), message = m.group(4);
                if (firstTime == null)
                {
                    firstTime = time;
                }
                lastTime = time;
                Integer n = levels.get(level);
                levels.put(level, n == null ? 1 : n + 1);
                if (version == null && message.contains("RimWorld 1."))
                {
                    version = cut(message, 160);
                }
                open = null;
                if (!level.equals("ERROR") && !level.equals("FATAL") && !level.equals("CRITICAL"))
                {
                    continue;
                }
                String key = NUMBERS.matcher(cut(message, 160)).replaceAll("#");
                Group group = groups.get(key);
                if (group == null)
                {
                    group = new Group();
                    group.message = cut(message, 300);
                    group.first = time;
                    group.order = groups.size();
                    groups.put(key, group);
                    open = group;
                }
                group.count++;
                group.last = time;
            }
        }
        finally
        {
            in.close();
        }
        text.append(lines).append(" 行，").append(firstTime != null ? firstTime + " 到 " + lastTime : "没有带时间的行").append("；");
        for (Map.Entry<String, Integer> level : levels.entrySet())
        {
            text.append(' ').append(level.getKey()).append(' ').append(level.getValue());
        }
        text.append("；不同的错误 ").append(groups.size()).append(" 种\n");
        if (version != null)
        {
            text.append(version).append('\n');
        }
        if (groups.isEmpty())
        {
            return;
        }
        List<Group> all = new ArrayList<Group>(groups.values());
        Collections.sort(all, new Comparator<Group>()
        {
            @Override
            public int compare(Group a, Group b)
            {
                return a.order - b.order;
            }
        });
        text.append("-- 最先出现的错误（按出现顺序，前 15 种）--\n");
        for (int i = 0; i < Math.min(15, all.size()); i++)
        {
            appendGroup(text, all.get(i));
        }
        Collections.sort(all, new Comparator<Group>()
        {
            @Override
            public int compare(Group a, Group b)
            {
                return b.count != a.count ? b.count - a.count : a.order - b.order;
            }
        });
        text.append("-- 次数最多的错误（前 25 种）--\n");
        for (int i = 0; i < Math.min(25, all.size()); i++)
        {
            appendGroup(text, all.get(i));
        }
    }

    private static void appendGroup(StringBuilder text, Group group)
    {
        text.append(String.format(Locale.US, "%6d×  %s%s  ", group.count, group.first.substring(11), group.count > 1 ? "-" + group.last.substring(11) : "")).append(group.message).append('\n');
        if (group.frame != null)
        {
            text.append("         ").append(group.frame).append('\n');
        }
    }

    private static boolean isPlumbing(String frame)
    {
        for (String plumbing : PLUMBING)
        {
            if (frame.startsWith(plumbing))
            {
                return true;
            }
        }
        return false;
    }

    /** The port's own log: its lines that say something failed, and the start's timing. */
    private static void portLog(StringBuilder text, File file, String title) throws IOException
    {
        text.append("\n== ").append(title).append(" ==\n");
        if (!file.isFile())
        {
            text.append("（无）\n");
            return;
        }
        Pattern bad = Pattern.compile("(?i)fail|error|exception|not found|could not|cannot|left out|(?-i:NOT )|崩溃|错误");
        List<String> found = new ArrayList<String>();
        BufferedReader in = new BufferedReader(new InputStreamReader(new FileInputStream(file), UTF8));
        try
        {
            String line;
            while ((line = in.readLine()) != null)
            {
                if (line.contains("main menu ") && line.contains(" s after the runtime started") || line.contains("[mobile] of that:") || bad.matcher(line).find())
                {
                    if (found.size() < 40 && !found.contains(line))
                    {
                        found.add(cut(line, 300));
                    }
                }
            }
        }
        finally
        {
            in.close();
        }
        for (String line : found)
        {
            text.append(line).append('\n');
        }
        if (found.isEmpty())
        {
            text.append("（没有出错的行）\n");
        }
    }

    /** PortMobile's record: conflicts, slow frames, gaps and the count of errors, and how the run ended. */
    private static void record(StringBuilder text, File file, String title) throws IOException
    {
        text.append("\n== ").append(title).append(" ==\n");
        if (!file.isFile())
        {
            text.append("（无）\n");
            return;
        }
        Map<String, List<String>> kinds = new LinkedHashMap<String, List<String>>();
        for (String kind : new String[] { "conflict", "slow", "gap" })
        {
            kinds.put(kind, new ArrayList<String>());
        }
        int errors = 0;
        List<String> tail = new ArrayList<String>();
        String head = null;
        BufferedReader in = new BufferedReader(new InputStreamReader(new FileInputStream(file), UTF8));
        try
        {
            String line;
            while ((line = in.readLine()) != null)
            {
                if (head == null)
                {
                    head = line;
                }
                String[] parts = line.trim().split("\\s+", 4);
                if (parts.length >= 3)
                {
                    if (parts[2].equals("error"))
                    {
                        errors++;
                    }
                    List<String> list = kinds.get(parts[2]);
                    if (list != null && list.size() < 25)
                    {
                        list.add(cut(line, 400));
                    }
                }
                tail.add(line);
                if (tail.size() > 12)
                {
                    tail.remove(0);
                }
            }
        }
        finally
        {
            in.close();
        }
        if (head != null)
        {
            text.append(cut(head, 300)).append('\n');
        }
        text.append("记录到的错误 ").append(errors).append(" 条（每种只记一次）\n");
        for (Map.Entry<String, List<String>> kind : kinds.entrySet())
        {
            if (kind.getValue().isEmpty())
            {
                continue;
            }
            text.append("-- ").append(kind.getKey()).append(" --\n");
            for (String line : kind.getValue())
            {
                text.append(line).append('\n');
            }
        }
        text.append("-- 最后 12 行 --\n");
        for (String line : tail)
        {
            text.append(cut(line, 400)).append('\n');
        }
    }

    /**
     * What the runtime wrote before it ended the process (a stack overflow with the repeating frames, a fatal error, an
     * unhandled exception): the tombstone of such an abort shows only the runtime's own frames, and some phones (a
     * tester's vivo) let an app read nothing of logcat.
     */
    private static void runtimeLastWords(StringBuilder text, File file, String title) throws IOException
    {
        text.append("\n== ").append(title).append(" ==\n");
        if (!file.isFile())
        {
            text.append("（无）\n");
            return;
        }
        int shown = 0;
        BufferedReader in = new BufferedReader(new InputStreamReader(new FileInputStream(file), UTF8));
        try
        {
            String line;
            while ((line = in.readLine()) != null && shown < 60)
            {
                if (shown == 0 && !(line.startsWith("Stack overflow") || line.startsWith("Fatal error") || line.startsWith("Unhandled exception")
                    || line.startsWith("Process terminated")))
                {
                    continue;
                }
                text.append(cut(line, 300)).append('\n');
                shown++;
            }
        }
        finally
        {
            in.close();
        }
        if (shown == 0)
        {
            text.append("（运行时没有留下结束进程的原因）\n");
        }
    }

    /** The watch (PortHooks' Watch): every stall, and the last lines of the load. */
    private static void watch(StringBuilder text, File file, String title) throws IOException
    {
        text.append("\n== ").append(title).append(" ==\n");
        if (!file.isFile())
        {
            text.append("（无）\n");
            return;
        }
        List<String> stalls = new ArrayList<String>(), tail = new ArrayList<String>();
        BufferedReader in = new BufferedReader(new InputStreamReader(new FileInputStream(file), UTF8));
        try
        {
            String line;
            while ((line = in.readLine()) != null)
            {
                if (line.contains(" stall ") && stalls.size() < 30)
                {
                    stalls.add(cut(line, 500));
                }
                tail.add(line);
                if (tail.size() > 6)
                {
                    tail.remove(0);
                }
            }
        }
        finally
        {
            in.close();
        }
        if (!stalls.isEmpty())
        {
            text.append("-- 卡住（主线程 10 秒以上没有画面）--\n");
            for (String line : stalls)
            {
                text.append(line).append('\n');
            }
        }
        text.append("-- 最后 6 行 --\n");
        for (String line : tail)
        {
            text.append(cut(line, 500)).append('\n');
        }
    }

    /**
     * mods.txt: the folders in the game's Mods with About.xml's name, packageId and supported versions, the DLLs in
     * their Assemblies folders, and the place in ModsConfig.xml's load order; then the active ids with no folder.
     */
    static String mods(File game, File modsConfig)
    {
        StringBuilder text = new StringBuilder();
        try
        {
            List<String> active = new ArrayList<String>();
            String config = readText(modsConfig, 1 << 20);
            Matcher activeMods = Pattern.compile("(?s)<activeMods>(.*?)</activeMods>").matcher(config);
            if (activeMods.find())
            {
                Matcher li = Pattern.compile("<li>\\s*([^<]+?)\\s*</li>").matcher(activeMods.group(1));
                while (li.find())
                {
                    active.add(li.group(1).toLowerCase(Locale.ROOT));
                }
            }
            List<String[]> rows = new ArrayList<String[]>();
            List<String> found = new ArrayList<String>();
            int notFor17 = 0;
            for (String root : new String[] { "Data", "Mods" })
            {
                File[] folders = new File(game, root).listFiles();
                if (folders == null)
                {
                    continue;
                }
                for (File folder : folders)
                {
                    if (!folder.isDirectory())
                    {
                        continue;
                    }
                    String about = readText(new File(folder, "About/About.xml"), 256 << 10);
                    String versions = list(about, "supportedVersions");
                    // The mod's own packageId and name, not those of the dependencies listed before them (<li> items).
                    String own = about.replaceAll("(?s)<li>.*?</li>", "");
                    String id = tag(own, "packageId").toLowerCase(Locale.ROOT);
                    String name = tag(own, "name");
                    int place = active.indexOf(id);
                    found.add(id);
                    boolean for17 = versions.contains("1.7") || root.equals("Data");
                    if (!for17)
                    {
                        notFor17++;
                    }
                    String order = place >= 0 ? String.format(Locale.US, "%4d", place + 1) : "   -";
                    rows.add(new String[] { place >= 0 ? String.format(Locale.US, "%05d", place) : "z" + folder.getName(),
                        order + "  " + (id.isEmpty() ? "（没有 packageId）" : id) + "  " + cut(name.isEmpty() ? folder.getName() : name, 60) + "  [" + versions + "]" + (for17 ? "" : " 未标 1.7")
                            + "  " + root + "/" + folder.getName() + dlls(folder) + (about.isEmpty() ? "  About.xml 读不到" : "") });
                }
            }
            Collections.sort(rows, new Comparator<String[]>()
            {
                @Override
                public int compare(String[] a, String[] b)
                {
                    return a[0].compareTo(b[0]);
                }
            });
            int on = 0;
            for (String id : active)
            {
                if (found.contains(id))
                {
                    on++;
                }
            }
            text.append("Mod 文件夹 ").append(rows.size()).append(" 个，启用 ").append(active.size()).append(" 个（找到文件夹的 ").append(on).append(" 个），未标 1.7 的 ").append(notFor17).append(" 个\n");
            text.append("顺序  packageId  名称  [支持版本]  文件夹  DLL\n");
            for (String[] row : rows)
            {
                text.append(row[1]).append('\n');
            }
            List<String> missing = new ArrayList<String>();
            for (String id : active)
            {
                if (!found.contains(id))
                {
                    missing.add(id);
                }
            }
            if (!missing.isEmpty())
            {
                text.append("\n启用了但手机上没有文件夹的：").append(missing).append('\n');
            }
        }
        catch (Throwable t)
        {
            text.append("（没写完：").append(t).append("）\n");
        }
        return text.toString();
    }

    /** The DLLs of a mod's Assemblies folders (its own, Common's and each version folder's): count and size. */
    private static String dlls(File folder)
    {
        int count = 0;
        long bytes = 0;
        List<File> places = new ArrayList<File>();
        places.add(new File(folder, "Assemblies"));
        File[] children = folder.listFiles();
        if (children != null)
        {
            for (File child : children)
            {
                String name = child.getName();
                if (child.isDirectory() && (name.matches("v?1\\.\\d+") || name.equalsIgnoreCase("Common")))
                {
                    places.add(new File(child, "Assemblies"));
                }
            }
        }
        StringBuilder which = new StringBuilder();
        for (File place : places)
        {
            File[] files = place.listFiles();
            if (files == null)
            {
                continue;
            }
            int here = 0;
            for (File file : files)
            {
                if (file.getName().toLowerCase(Locale.ROOT).endsWith(".dll"))
                {
                    here++;
                    bytes += file.length();
                }
            }
            if (here > 0)
            {
                count += here;
                which.append(which.length() == 0 ? "" : ",").append(place.getParentFile().equals(folder) ? "" : place.getParentFile().getName() + "/").append(here);
            }
        }
        return count == 0 ? "" : String.format(Locale.US, "  DLL %d (%s, %d KB)", count, which, bytes >> 10);
    }

    private static String tag(String xml, String name)
    {
        Matcher m = Pattern.compile("(?is)<" + name + ">\\s*(.*?)\\s*</" + name + ">").matcher(xml);
        return m.find() ? m.group(1).replaceAll("\\s+", " ") : "";
    }

    private static String list(String xml, String name)
    {
        StringBuilder items = new StringBuilder();
        Matcher li = Pattern.compile("<li>\\s*([^<]*?)\\s*</li>").matcher(tag(xml, name));
        while (li.find())
        {
            items.append(items.length() == 0 ? "" : " ").append(li.group(1));
        }
        return items.toString();
    }

    private static String readText(File file, int limit)
    {
        try
        {
            if (!file.isFile())
            {
                return "";
            }
            FileInputStream in = new FileInputStream(file);
            try
            {
                byte[] bytes = new byte[(int) Math.min(limit, file.length())];
                int read = 0;
                while (read < bytes.length)
                {
                    int n = in.read(bytes, read, bytes.length - read);
                    if (n <= 0)
                    {
                        break;
                    }
                    read += n;
                }
                return new String(bytes, 0, read, UTF8);
            }
            finally
            {
                in.close();
            }
        }
        catch (IOException e)
        {
            return "";
        }
    }

    private static String cut(String text, int max)
    {
        return text.length() > max ? text.substring(0, max) + "…" : text;
    }
}

package com.local.waylith.port;

import android.app.ActivityManager;
import android.app.AlertDialog;
import android.app.Dialog;
import android.app.NativeActivity;
import android.content.DialogInterface;
import android.content.ClipData;
import android.content.ComponentName;
import android.content.ContentResolver;
import android.content.Context;
import android.content.Intent;
import android.database.Cursor;
import android.hardware.input.InputManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.DocumentsContract;
import android.provider.OpenableColumns;
import android.text.SpannableStringBuilder;
import android.text.Spanned;
import android.text.method.LinkMovementMethod;
import android.text.style.ClickableSpan;
import android.view.InputDevice;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.TextView;
import android.widget.Toast;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.Charset;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/**
 * The game's activity: a NativeActivity (the host library is named by the manifest's android.app.lib_name) that
 * takes the whole screen. Without this the window is laid out away from the display cutout (141 px on this phone)
 * and the status and navigation bars stay over the game. The screen is kept on while the game runs.
 *
 * It also fetches the files the player picks with the system's file chooser (pickFiles below). An app may not read
 * the phone's shared storage by path; what the chooser hands back can only be opened through the system, and the
 * chooser's answer arrives in onActivityResult, which only the activity receives. So the copying is done here. What
 * the copied files are (mods to import) is up to the game side (mobile/ModImport.cs), which starts a pick through the
 * GLFW shim and then watches the state file this writes.
 */
public final class MainActivity extends NativeActivity
{
    private TextInputView textInput;

    @Override
    protected void onCreate(Bundle savedInstanceState)
    {
        super.onCreate(savedInstanceState);
        WindowManager.LayoutParams attributes = getWindow().getAttributes();
        if (Build.VERSION.SDK_INT >= 30)
        {
            attributes.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS;
        }
        else if (Build.VERSION.SDK_INT >= 28)
        {
            attributes.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
        }
        getWindow().setAttributes(attributes);
        if (Build.VERSION.SDK_INT >= 28)
        {
            // Where the camera's hole is, for the game to keep its interface clear of it (cutoutRects): the insets come
            // again whenever the screen turns (sensorLandscape puts the hole on the other side).
            getWindow().getDecorView().setOnApplyWindowInsetsListener(new View.OnApplyWindowInsetsListener()
            {
                @Override
                public android.view.WindowInsets onApplyWindowInsets(View view, android.view.WindowInsets insets)
                {
                    noteCutout(insets);
                    return view.onApplyWindowInsets(insets);
                }
            });
        }
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        // The view the soft keyboard talks to (TextInputView), one pixel in the corner; the game's own view keeps the touches.
        textInput = new TextInputView(this);
        addContentView(textInput, new ViewGroup.LayoutParams(1, 1));
        // The mice and keyboards plugged in or paired, for the game to switch to the PC's ways while one is (inputDevices).
        InputManager inputManager = (InputManager) getSystemService(INPUT_SERVICE);
        if (inputManager != null)
        {
            inputManager.registerInputDeviceListener(inputDeviceListener, null);
        }
        noteInputDevices();
        hideSystemBars();
        // A report in Download/Port17 when the last run ended badly, and the "导出日志" shortcut on the icon.
        Diagnostics.onStart(this);
        Diagnostics.trail(this, "started" + (savedInstanceState != null ? " again by the system (the process had been ended)" : "") + (Diagnostics.activitiesNotKept(this) ? "; developer option 不保留活动 is on" : ""));
        if (Diagnostics.activitiesNotKept(this))
        {
            Toast.makeText(this, "手机的开发者选项里开着「不保留活动」（有的手机叫「不保留 activity」）：切到别的应用或打开文件选择器时，游戏会被系统关掉。请先关掉这个选项。", Toast.LENGTH_LONG).show();
        }
        GameService.start(this);
    }

    /** The display cutout's rectangles in window pixels, "left,top,right,bottom;..." ("" without one), as of the last insets. */
    private volatile String cutoutRects = "";

    private void noteCutout(android.view.WindowInsets insets)
    {
        android.view.DisplayCutout cutout = Build.VERSION.SDK_INT >= 28 && insets != null ? insets.getDisplayCutout() : null;
        StringBuilder text = new StringBuilder();
        if (cutout != null)
        {
            for (android.graphics.Rect rect : cutout.getBoundingRects())
            {
                if (!rect.isEmpty())
                {
                    text.append(text.length() > 0 ? ";" : "").append(rect.left).append(',').append(rect.top).append(',').append(rect.right).append(',').append(rect.bottom);
                }
            }
        }
        cutoutRects = text.toString();
    }

    /** The external mice and keyboards: "<bits>|<names>" (bit 1 a mouse, bit 2 a keyboard), as of the last change. */
    private volatile String inputDevices = "0|";

    private final InputManager.InputDeviceListener inputDeviceListener = new InputManager.InputDeviceListener()
    {
        @Override
        public void onInputDeviceAdded(int deviceId)
        {
            noteInputDevices();
        }

        @Override
        public void onInputDeviceRemoved(int deviceId)
        {
            noteInputDevices();
        }

        @Override
        public void onInputDeviceChanged(int deviceId)
        {
            noteInputDevices();
        }
    };

    /**
     * A mouse is a device with the mouse source (a touchpad on a keyboard counts), a keyboard one with letter keys; only
     * external ones count (plugged in or paired: the phone's own buttons and sensors report themselves as devices too).
     */
    private void noteInputDevices()
    {
        int bits = 0;
        StringBuilder names = new StringBuilder();
        for (int id : InputDevice.getDeviceIds())
        {
            InputDevice device = InputDevice.getDevice(id);
            if (device == null || device.isVirtual() || !isExternal(device))
            {
                continue;
            }
            boolean mouse = device.supportsSource(InputDevice.SOURCE_MOUSE);
            boolean keyboard = device.getKeyboardType() == InputDevice.KEYBOARD_TYPE_ALPHABETIC;
            if (!mouse && !keyboard)
            {
                continue;
            }
            bits |= (mouse ? 1 : 0) | (keyboard ? 2 : 0);
            names.append(names.length() > 0 ? ", " : "").append(device.getName().replace('|', '/')).append(mouse && keyboard ? " (mouse, keyboard)" : mouse ? " (mouse)" : " (keyboard)");
        }
        String now = bits + "|" + names;
        if (!now.equals(inputDevices))
        {
            android.util.Log.i("port17", "input devices: " + now);
        }
        inputDevices = now;
    }

    private static boolean isExternal(InputDevice device)
    {
        if (Build.VERSION.SDK_INT >= 29)
        {
            return device.isExternal();
        }
        try
        {
            return (Boolean) InputDevice.class.getMethod("isExternal").invoke(device);
        }
        catch (Throwable e)
        {
            return true;
        }
    }

    /** Called by the native side (glfwAndroidInputDevices, about once a second): the external mice and keyboards, for mobile/InputMode.cs. */
    public String inputDevices()
    {
        return inputDevices;
    }

    /**
     * Called by the native side (the GLFW shim's glfwAndroidCutout, about once a second): where the camera's hole is now,
     * for mobile/Cutout.cs. Before the first insets arrive they are asked for here.
     */
    public String cutoutRects()
    {
        if (cutoutRects.isEmpty() && Build.VERSION.SDK_INT >= 28)
        {
            noteCutout(getWindow().getDecorView().getRootWindowInsets());
        }
        return cutoutRects;
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus)
    {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus)
        {
            hideSystemBars();
        }
    }

    // In the trail (logs/import.txt, in the zip of "导出日志"): when the app was away, and when the system wanted memory
    // back, to read a report against (the game's own record, logs/record.txt, shows the gap in its frames).
    @Override
    protected void onPause()
    {
        super.onPause();
        Diagnostics.trail(this, "the app leaves the screen");
    }

    @Override
    protected void onResume()
    {
        super.onResume();
        Diagnostics.trail(this, "the app is on the screen");
    }

    @Override
    public void onTrimMemory(int level)
    {
        super.onTrimMemory(level);
        Diagnostics.trail(this, "the system asks for memory back (level " + level + ")");
    }

    private static final int PICK_FILES = 7101, PICK_FOLDER = 7102;

    private static final int IDLE = 0, CHOOSING = 1, COPYING = 2, DONE = 3, FAILED = 4, CANCELLED = 5;

    /** Where a pick stands (the constants above), how much has been copied, and what went wrong or was left out. */
    private volatile int pickState = IDLE;
    private final AtomicInteger pickCount = new AtomicInteger();
    private final AtomicLong pickBytes = new AtomicLong();
    private volatile String pickMessage = "";
    private volatile long pickStateWritten;

    /** What was picked is copied here (files/ModImport); the game side empties the folder before a pick and after. */
    private File pickTarget()
    {
        return new File(getExternalFilesDir(null), "ModImport");
    }

    /**
     * The game side reads the state from this file (files/ModImport.state): "state count bytes" on the first line, the
     * message after it. Written whole to a temporary file and renamed, so a reader never sees half of it.
     */
    private synchronized void writePickState()
    {
        try
        {
            File dir = getExternalFilesDir(null);
            File temp = new File(dir, "ModImport.state.tmp");
            OutputStream out = new FileOutputStream(temp);
            try
            {
                out.write((pickState + " " + pickCount.get() + " " + pickBytes.get() + "\n" + pickMessage).getBytes("UTF-8"));
            }
            finally
            {
                out.close();
            }
            temp.renameTo(new File(dir, "ModImport.state"));
            pickStateWritten = System.currentTimeMillis();
        }
        catch (IOException ignored)
        {
        }
    }

    private void setPickState(int state, String message)
    {
        pickMessage = message == null ? "" : message;
        pickState = state;
        writePickState();
    }

    /**
     * Opens the system's chooser for archives (several at once) or for one folder; called from the native side (the
     * GLFW shim's glfwAndroidPickFiles). Every chosen ".zip" is unpacked into a folder of its name below
     * files/ModImport; a chosen folder is copied there with everything in it. Returns at once.
     */
    public void pickFiles(final boolean folder)
    {
        pickCount.set(0);
        pickBytes.set(0);
        setPickState(CHOOSING, "");
        runOnUiThread(new Runnable()
        {
            @Override
            public void run()
            {
                try
                {
                    Intent intent;
                    if (folder)
                    {
                        intent = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE);
                    }
                    else
                    {
                        intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
                        intent.addCategory(Intent.CATEGORY_OPENABLE);
                        // Not "application/zip": file managers and downloads label archives in several ways,
                        // and a file the chooser greys out cannot be picked at all.
                        intent.setType("*/*");
                        intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
                    }
                    startActivityForResult(intent, folder ? PICK_FOLDER : PICK_FILES);
                }
                catch (Throwable problem)
                {
                    setPickState(FAILED, String.valueOf(problem));
                }
            }
        });
    }

    @Override
    protected void onActivityResult(final int request, int result, final Intent data)
    {
        super.onActivityResult(request, result, data);
        if (request == PICK_GAME_ZIP || request == PICK_GAME_FOLDER)
        {
            onGamePicked(request, result, data);
            return;
        }
        if (request != PICK_FILES && request != PICK_FOLDER)
        {
            return;
        }
        if (result != RESULT_OK || data == null)
        {
            setPickState(CANCELLED, "");
            return;
        }
        final List<Uri> chosen = new ArrayList<Uri>();
        ClipData several = data.getClipData();
        if (several != null)
        {
            for (int i = 0; i < several.getItemCount(); i++)
            {
                chosen.add(several.getItemAt(i).getUri());
            }
        }
        else if (data.getData() != null)
        {
            chosen.add(data.getData());
        }
        if (chosen.isEmpty())
        {
            setPickState(CANCELLED, "");
            return;
        }
        setPickState(COPYING, "");
        final File target = pickTarget();
        new Thread(new Runnable()
        {
            @Override
            public void run()
            {
                try
                {
                    StringBuilder leftOut = new StringBuilder();
                    for (Uri uri : chosen)
                    {
                        if (request == PICK_FOLDER)
                        {
                            String id = DocumentsContract.getTreeDocumentId(uri);
                            Uri root = DocumentsContract.buildDocumentUriUsingTree(uri, id);
                            File into = new File(target, safeName(nameOf(root, "folder")));
                            into.mkdirs();
                            copyTreeInParallel(uri, id, into);
                        }
                        else
                        {
                            String name = nameOf(uri, "archive.zip");
                            if (name.toLowerCase().endsWith(".zip"))
                            {
                                unpack(uri, new File(target, safeName(name.substring(0, name.length() - 4))));
                            }
                            else if (name.toLowerCase().endsWith(".rws"))
                            {
                                // A save (mobile/SaveImport.cs puts it with the saves): copied as it is. A file a file
                                // manager put into the saves folder may not be readable by the game (a tester's vivo).
                                DocumentReader reader = new DocumentReader(getContentResolver(), uri);
                                InputStream in;
                                try
                                {
                                    in = reader.open(uri);
                                }
                                finally
                                {
                                    reader.close();
                                }
                                try
                                {
                                    write(in, new File(target, safeName(name)));
                                }
                                finally
                                {
                                    in.close();
                                }
                            }
                            else
                            {
                                leftOut.append(name).append('\n');
                            }
                        }
                    }
                    setPickState(DONE, leftOut.toString());
                }
                catch (Throwable problem)
                {
                    setPickState(FAILED, String.valueOf(problem));
                }
            }
        }, "PortPick").start();
    }

    private String nameOf(Uri uri, String fallback)
    {
        Cursor cursor = null;
        try
        {
            cursor = getContentResolver().query(uri, new String[] { OpenableColumns.DISPLAY_NAME }, null, null, null);
            if (cursor != null && cursor.moveToFirst() && cursor.getString(0) != null)
            {
                return cursor.getString(0);
            }
        }
        catch (Exception ignored)
        {
        }
        finally
        {
            if (cursor != null)
            {
                cursor.close();
            }
        }
        return fallback;
    }

    private static String safeName(String name)
    {
        String cleaned = name.replaceAll("[\\\\/:*?\"<>|]", "_").trim();
        return cleaned.isEmpty() || cleaned.equals(".") || cleaned.equals("..") ? "item" : cleaned;
    }

    /**
     * The system's file provider takes its time over every single file (about 23 a second on the test phone, whatever
     * their size), and a mod is thousands of small textures: the folders are walked here, the files copied by a few
     * threads at once. The first failure stops the rest and is reported. The provider is reached through DocumentReader,
     * so that its process dying does not end this one.
     */
    private void copyTreeInParallel(Uri tree, String documentId, File into) throws IOException
    {
        ExecutorService pool = Executors.newFixedThreadPool(COPY_THREADS);
        AtomicReference<Throwable> failure = new AtomicReference<Throwable>();
        DocumentReader reader = new DocumentReader(getContentResolver(), tree);
        try
        {
            try
            {
                copyTree(reader, tree, documentId, into, pool, failure);
            }
            finally
            {
                pool.shutdown();
            }
            while (!pool.awaitTermination(1, TimeUnit.SECONDS))
            {
                if (failure.get() != null)
                {
                    pool.shutdownNow();
                }
            }
        }
        catch (InterruptedException interrupted)
        {
            pool.shutdownNow();
            throw new IOException("interrupted");
        }
        finally
        {
            reader.close();
        }
        Throwable problem = failure.get();
        if (problem != null)
        {
            throw problem instanceof IOException ? (IOException)problem : new IOException(String.valueOf(problem));
        }
    }

    private static final int COPY_THREADS = 8;

    private void copyTree(final DocumentReader reader, final Uri tree, String documentId, File into, ExecutorService pool, final AtomicReference<Throwable> failure) throws IOException
    {
        Uri children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, documentId);
        List<String[]> rows = reader.query(children, new String[] { DocumentsContract.Document.COLUMN_DOCUMENT_ID,
            DocumentsContract.Document.COLUMN_DISPLAY_NAME, DocumentsContract.Document.COLUMN_MIME_TYPE });
        for (String[] row : rows)
        {
            String id = row[0];
            File out = new File(into, safeName(row[1] != null ? row[1] : "item"));
            if (failure.get() != null)
            {
                return;
            }
            if (DocumentsContract.Document.MIME_TYPE_DIR.equals(row[2]))
            {
                out.mkdirs();
                copyTree(reader, tree, id, out, pool, failure);
            }
            else
            {
                final String fileId = id;
                final File target = out;
                pool.execute(new Runnable()
                {
                    @Override
                    public void run()
                    {
                        if (failure.get() != null)
                        {
                            return;
                        }
                        try
                        {
                            InputStream in = reader.open(DocumentsContract.buildDocumentUriUsingTree(tree, fileId));
                            try
                            {
                                write(in, target);
                            }
                            finally
                            {
                                in.close();
                            }
                        }
                        catch (Throwable problem)
                        {
                            failure.compareAndSet(null, problem);
                        }
                    }
                });
            }
        }
    }

    // Archives made on a Chinese Windows often carry file names in GBK without saying so, others UTF-8 without saying
    // so: the names are read undecoded and worked out one by one (ZipNames).
    private void unpack(Uri uri, File into) throws IOException
    {
        // Opened through DocumentReader: a game's archive takes minutes, and its provider's process dying meanwhile would
        // otherwise end this one.
        DocumentReader reader = new DocumentReader(getContentResolver(), uri);
        InputStream raw;
        try
        {
            raw = reader.open(uri);
        }
        finally
        {
            reader.close();
        }
        ZipInputStream zip = new ZipInputStream(raw, ZipNames.RAW);
        try
        {
            into.mkdirs();
            String base = into.getCanonicalPath() + File.separator;
            ZipEntry entry;
            while ((entry = zip.getNextEntry()) != null)
            {
                String name = ZipNames.decode(entry.getName());
                File out = new File(into, name.replace('\\', '/'));
                // An entry must not climb out of the folder it is unpacked into.
                if (!out.getCanonicalPath().startsWith(base))
                {
                    continue;
                }
                if (entry.isDirectory() || name.endsWith("/") || name.endsWith("\\"))
                {
                    out.mkdirs();
                }
                else
                {
                    File parent = out.getParentFile();
                    if (parent != null)
                    {
                        parent.mkdirs();
                    }
                    write(zip, out);
                }
            }
        }
        finally
        {
            zip.close();
        }
    }

    /**
     * The same as unpack, read through the archive's central directory (java.util.zip.ZipFile) rather than its local
     * headers one after another: an archive a stream reader finds nothing in. ZipFile wants a file: the chosen one's
     * descriptor by /proc/self/fd (refused on a tester's Xiaomi: EACCES), else a copy next to the import folder.
     */
    private void unpackByDirectory(Uri uri, File into, File importDir) throws IOException
    {
        android.os.ParcelFileDescriptor descriptor = getContentResolver().openFileDescriptor(uri, "r");
        if (descriptor == null)
        {
            throw new IOException("cannot open " + uri);
        }
        File copy = new File(importDir.getParentFile(), "import-archive.tmp");
        try
        {
            java.util.zip.ZipFile zip;
            try
            {
                zip = new java.util.zip.ZipFile(new File("/proc/self/fd/" + descriptor.getFd()), java.util.zip.ZipFile.OPEN_READ, ZipNames.RAW);
            }
            catch (IOException refused)
            {
                Diagnostics.trail(this, "by its descriptor: " + refused.getMessage() + "; copying the archive first");
                InputStream in = getContentResolver().openInputStream(uri);
                if (in == null)
                {
                    throw new IOException("cannot open " + uri);
                }
                try
                {
                    OutputStream out = new FileOutputStream(copy);
                    try
                    {
                        byte[] buffer = new byte[1 << 20];
                        for (int n; (n = in.read(buffer)) > 0; )
                        {
                            out.write(buffer, 0, n);
                        }
                    }
                    finally
                    {
                        out.close();
                    }
                }
                finally
                {
                    in.close();
                }
                zip = new java.util.zip.ZipFile(copy, java.util.zip.ZipFile.OPEN_READ, ZipNames.RAW);
            }
            try
            {
                into.mkdirs();
                String base = into.getCanonicalPath() + File.separator;
                java.util.Enumeration<? extends ZipEntry> entries = zip.entries();
                while (entries.hasMoreElements())
                {
                    ZipEntry entry = entries.nextElement();
                    String name = ZipNames.decode(entry.getName());
                    File out = new File(into, name.replace('\\', '/'));
                    if (!out.getCanonicalPath().startsWith(base))
                    {
                        continue;
                    }
                    if (entry.isDirectory() || name.endsWith("/") || name.endsWith("\\"))
                    {
                        out.mkdirs();
                        continue;
                    }
                    File parent = out.getParentFile();
                    if (parent != null)
                    {
                        parent.mkdirs();
                    }
                    InputStream in = zip.getInputStream(entry);
                    try
                    {
                        write(in, out);
                    }
                    finally
                    {
                        in.close();
                    }
                }
            }
            finally
            {
                zip.close();
            }
        }
        finally
        {
            descriptor.close();
            copy.delete();
        }
    }

    /** What a chosen file is when its first bytes say it is another kind of archive or a program, else null. */
    private String otherArchive(Uri uri)
    {
        byte[] head = new byte[6];
        int n = 0;
        try
        {
            InputStream in = getContentResolver().openInputStream(uri);
            if (in != null)
            {
                n = in.read(head);
                in.close();
            }
        }
        catch (IOException ignored)
        {
        }
        if (n >= 2 && head[0] == 'M' && head[1] == 'Z')
        {
            return "一个 Windows 程序（.exe，多半是自解压包）";
        }
        if (n >= 4 && head[0] == 'R' && head[1] == 'a' && head[2] == 'r' && head[3] == '!')
        {
            return "一个 RAR 压缩包";
        }
        if (n >= 6 && head[0] == '7' && head[1] == 'z' && (head[2] & 0xff) == 0xbc && (head[3] & 0xff) == 0xaf)
        {
            return "一个 7z 压缩包";
        }
        return null;
    }

    /** The size of a chosen file and its first bytes, for telling what it is. */
    private String describeFile(Uri uri)
    {
        StringBuilder text = new StringBuilder();
        Cursor cursor = null;
        try
        {
            cursor = getContentResolver().query(uri, new String[] { OpenableColumns.SIZE }, null, null, null);
            if (cursor != null && cursor.moveToFirst() && !cursor.isNull(0))
            {
                text.append(cursor.getLong(0) >> 20).append(" MB");
            }
        }
        catch (Exception ignored)
        {
        }
        finally
        {
            if (cursor != null)
            {
                cursor.close();
            }
        }
        try
        {
            InputStream in = getContentResolver().openInputStream(uri);
            if (in != null)
            {
                byte[] head = new byte[8];
                int n = in.read(head);
                in.close();
                text.append(text.length() > 0 ? "，" : "").append("开头");
                for (int i = 0; i < n; i++)
                {
                    text.append(String.format(java.util.Locale.US, " %02x", head[i] & 0xff));
                }
            }
        }
        catch (Exception e)
        {
            text.append(text.length() > 0 ? "，" : "").append("读不了：").append(e.getMessage());
        }
        return text.toString();
    }

    private void write(InputStream in, File out) throws IOException
    {
        OutputStream stream = new FileOutputStream(out);
        try
        {
            byte[] buffer = new byte[1 << 16];
            int n;
            while ((n = in.read(buffer)) > 0)
            {
                stream.write(buffer, 0, n);
                pickBytes.addAndGet(n);
            }
        }
        finally
        {
            stream.close();
        }
        pickCount.incrementAndGet();
        if (System.currentTimeMillis() - pickStateWritten > 250)
        {
            writePickState();
        }
    }

    private static void delete(File file)
    {
        File[] inside = file.listFiles();
        if (inside != null)
        {
            for (File child : inside)
            {
                delete(child);
            }
        }
        file.delete();
    }

    // ---- the tester's APK: the player's own copy of the game (PAYLOAD=port host/build.sh) ----

    private static final int PICK_GAME_ZIP = 7201, PICK_GAME_FOLDER = 7202;

    /** What the dialogs and the copying tell the waiting runtime thread: {"picked", root}, {"failed", why}, {"again"}, {"quit"}, {"ok"}. */
    private final LinkedBlockingQueue<String[]> gameEvents = new LinkedBlockingQueue<String[]>();
    private AlertDialog busyDialog;
    /** The dialog asking for the game while it is shown; and whether a chosen game is being copied (onGamePicked). */
    private AlertDialog askDialog;
    private volatile boolean gamePickRunning;
    /** The player chose other files over a game found in files/import (askForGame): not offered again in this process. */
    private volatile boolean placedDeclined;
    /** Set by prepareGame while a copy is asked for after "重新导入游戏" (ReimportActivity): the dialog says how to call it off. */
    private volatile boolean reimporting;
    /** While prepareGame asks for, copies or sets up the game (in this process). */
    private static volatile boolean importing;

    /**
     * The activity goes away while the game is imported (swiped away in the recent apps): the import ends with the
     * process. ImportService would otherwise keep the process copying for nobody, and opening the app again would start a
     * second import in the same process over the first (a tester's Samsung, 2026-10-21: the app swiped away during a
     * 16-minute copy and opened again, two copies into one folder, the first one's end closed its dialog on the gone
     * window and crashed the app, which cut the setup off half way). The next start asks for the game again.
     */
    @Override
    protected void onDestroy()
    {
        // Who ended it: the player or the app (finishing), or the system on its own (a phone short of memory, or the
        // developer option 不保留活动, which destroys every activity that leaves the screen).
        String by = isFinishing() ? " (finishing)" : " (by the system" + (Diagnostics.activitiesNotKept(this) ? ", developer option 不保留活动 on" : "") + ")";
        // And what the app's tasks hold now (a Xiaomi's chooser seemed to clear the task it was started in).
        StringBuilder tasks = new StringBuilder();
        try
        {
            for (ActivityManager.AppTask task : ((ActivityManager) getSystemService(ACTIVITY_SERVICE)).getAppTasks())
            {
                ActivityManager.RecentTaskInfo info = task.getTaskInfo();
                tasks.append(tasks.length() > 0 ? ", " : "").append(info.baseActivity != null ? info.baseActivity.flattenToShortString() : "?").append(" .. ")
                    .append(info.topActivity != null ? info.topActivity.flattenToShortString() : "?").append(" (").append(info.numActivities).append(')');
            }
        }
        catch (Throwable ignored)
        {
        }
        by += "; tasks: " + (tasks.length() > 0 ? tasks : "none");
        InputManager inputManager = (InputManager) getSystemService(INPUT_SERVICE);
        if (inputManager != null)
        {
            inputManager.unregisterInputDeviceListener(inputDeviceListener);
        }
        if (importing && !isChangingConfigurations())
        {
            endImport(this, "the activity went away during the import" + by);
        }
        else
        {
            Diagnostics.trail(this, "the activity is destroyed" + by);
        }
        super.onDestroy();
    }

    /** Ends the import: the setup process if one runs, then this process (announced, so it is not reported as a crash). */
    static void endImport(Context context, String why)
    {
        Diagnostics.trail(context, why + ": the import ends with the process");
        ActivityManager manager = (ActivityManager) context.getSystemService(ACTIVITY_SERVICE);
        List<ActivityManager.RunningAppProcessInfo> processes = manager != null ? manager.getRunningAppProcesses() : null;
        if (processes != null)
        {
            for (ActivityManager.RunningAppProcessInfo process : processes)
            {
                if (process.processName.endsWith(":setup"))
                {
                    android.os.Process.killProcess(process.pid);
                }
            }
        }
        Diagnostics.noteSelfExit(context);
        android.os.Process.killProcess(android.os.Process.myPid());
    }

    /**
     * Called by the host (host.c, on its runtime thread, before the runtime starts) when this is the tester's APK and
     * the game is not prepared (setup/version.txt differs from game/port-setup.txt). Asks for the player's own copy of
     * the game, copies it to files/import and has SetupService (setup/PortSetup.dll) make it ready; when it was imported
     * before and only the APK is newer, the kept assemblies are prepared again without asking. Returns true when the
     * game is ready, false when the player chose to leave (the activity is finishing then).
     */
    public boolean prepareGame()
    {
        File ext = getExternalFilesDir(null);
        String[] kept = new File(ext, "setup/original").list();
        // "重新导入游戏" (ReimportActivity) put game/port-setup.txt aside: asked for a copy even though one was imported.
        final File aside = new File(ext, ReimportActivity.ASIDE);
        boolean reimport = reimporting = aside.exists();
        // Imported before: a setup finished once (game/port-setup.txt, from older rules). A setup cut off half way leaves the
        // game's data moved and some assemblies kept but no port-setup.txt; preparing those again failed on what was
        // missing (a tester's Samsung: "Failed to resolve assembly: 'Brutal.Vulkan'"), so the game is asked for instead.
        boolean importedBefore = !reimport && new File(ext, "game/port-setup.txt").exists() && new File(ext, "game/Assets.zip").exists() && kept != null && kept.length > 0;
        if (!importedBefore && !reimport && (kept == null || kept.length == 0) && new File(ext, "game/Assembly-CSharp.dll").exists() && new File(ext, "game/Assets.zip").exists() && !new File(ext, "game/port-setup.txt").exists())
        {
            // A game made ready some other way (copied from the PC by host/stage.sh, or by a full APK): kept as it is.
            try
            {
                java.io.FileOutputStream out = new java.io.FileOutputStream(new File(ext, "game/port-setup.txt"));
                out.write((readText(new File(ext, "setup/version.txt")).split("\n")[0].trim() + "\nprepared elsewhere\n").getBytes("UTF-8"));
                out.close();
                return true;
            }
            catch (IOException ignored)
            {
            }
        }
        // From here to the end the process is a foreground one: the file chooser, or another app while the game is copied
        // and set up, would otherwise leave it to be ended in the background on a phone short of memory.
        Diagnostics.trail(this, "game not ready: " + (reimport ? "to be imported again" : importedBefore ? "imported before, prepared again" : "to be imported") + "; " + (ext.getUsableSpace() >> 20) + " MB free");
        GameService.stop(this);
        ImportService.start(this);
        importing = true;
        try
        {
            String problem = null;
            // Whether a copy went to the setup during a re-import: the game folder may have been changed, so it is not put back.
            boolean attempted = false;
            while (true)
            {
                String root = "";
                if (!importedBefore || problem != null)
                {
                    root = askForGame(problem);
                    if (root == null)
                    {
                        if (reimport && !attempted && aside.exists() && aside.renameTo(new File(ext, "game/port-setup.txt")))
                        {
                            // The re-import is called off before any copy was taken: the game is as it was.
                            Diagnostics.trail(this, "the player left: re-import called off");
                        }
                        else
                        {
                            Diagnostics.trail(this, "the player left");
                        }
                        leave();
                        return false;
                    }
                }
                attempted |= !root.isEmpty();
                String[] result = runSetup(root);
                Diagnostics.trail(this, "setup: " + result[0] + (result[1].isEmpty() ? "" : " - " + result[1].replace('\n', ' ')));
                if ("ok".equals(result[0]))
                {
                    aside.delete();
                    if (result[1].contains("注意"))
                    {
                        tell("游戏文件已准备好", result[1]);
                    }
                    else
                    {
                        toast("游戏文件已准备好，正在启动。" + (result[1].isEmpty() ? "" : "\n" + result[1]));
                    }
                    return true;
                }
                problem = result[1];
            }
        }
        finally
        {
            importing = false;
            ImportService.stop(this);
            GameService.start(this);
        }
    }

    /** The dialog that asks for the game, and what follows from it; the root of the copied game, or null to leave. */
    private String askForGame(String problem)
    {
        while (true)
        {
            if (gamePickRunning)
            {
                // The process was ended while the chooser was open and started again by the system to take its answer
                // (onActivityResult came first): the copying is under way, its result is waited for instead of asking.
                String[] event = takeGameEvent();
                if ("picked".equals(event[0]))
                {
                    return event[1];
                }
                if ("failed".equals(event[0]))
                {
                    problem = event[1];
                }
                continue;
            }
            gameEvents.clear();
            Diagnostics.trail(this, "asking for the game" + (problem != null ? " (last time: " + problem.replace('\n', ' ') + ")" : ""));
            final String shownProblem = problem;
            // A game put into files/import by hand (over USB from a PC), offered before the chooser: the way in for a phone
            // whose file chooser ends the app (a Xiaomi tester's HyperOS, 2026-10-23: the activity finished a second after
            // the chooser came up, every time). Not after a failed setup, which may have been that very copy.
            final File importDir = new File(getExternalFilesDir(null), "import");
            final File placed = problem == null && !placedDeclined ? findGame(importDir, 4) : null;
            if (placed != null)
            {
                Diagnostics.trail(this, "a game was put into the import folder: " + placed);
            }
            runOnUiThread(new Runnable()
            {
                @Override
                public void run()
                {
                    if (gamePickRunning)
                    {
                        // The chooser's answer arrived meanwhile: no dialog over the copying.
                        return;
                    }
                    if (placed != null)
                    {
                        String where = placed.getPath();
                        int at = where.indexOf("/Android/");
                        askDialog = new AlertDialog.Builder(MainActivity.this)
                            .setTitle("找到你放入的游戏文件")
                            .setMessage("在 " + (at >= 0 ? where.substring(at + 1) : where) + " 里找到了 RimWorld 的游戏文件。用它导入吗？\n\n"
                                + "导入时会把这些文件移进游戏目录，完成后这个文件夹会清空。")
                            .setCancelable(false)
                            .setPositiveButton("用它导入", new DialogInterface.OnClickListener()
                            {
                                public void onClick(DialogInterface dialog, int which)
                                {
                                    gameEvents.offer(new String[] { "picked", placed.getPath() });
                                }
                            })
                            .setNeutralButton("改选其他文件", new DialogInterface.OnClickListener()
                            {
                                public void onClick(DialogInterface dialog, int which)
                                {
                                    placedDeclined = true;
                                    gameEvents.offer(new String[] { "again" });
                                }
                            })
                            .setNegativeButton("退出", new DialogInterface.OnClickListener()
                            {
                                public void onClick(DialogInterface dialog, int which)
                                {
                                    gameEvents.offer(new String[] { "quit" });
                                }
                            })
                            .show();
                        return;
                    }
                    if (Diagnostics.activitiesNotKept(MainActivity.this))
                    {
                        // The system would destroy this activity under the file chooser, and the import with it (onDestroy):
                        // a Xiaomi tester's five tries, 2026-10-23, each ended a second after the chooser came up.
                        askDialog = new AlertDialog.Builder(MainActivity.this)
                            .setTitle("请先关掉「不保留活动」")
                            .setMessage("这台手机的开发者选项里开着「不保留活动」（有的手机叫「不保留 activity」，在开发者选项的“应用”一栏）。打开文件选择器时，系统会马上把游戏关掉，所以没法导入；玩的时候切到别的应用，游戏也会被关掉。\n\n"
                                + "请点下面的按钮（或到 设置 → 开发者选项，小米在 设置 → 更多设置 → 开发者选项）关掉它，再重新打开游戏。")
                            .setCancelable(false)
                            .setPositiveButton("打开开发者选项", new DialogInterface.OnClickListener()
                            {
                                public void onClick(DialogInterface dialog, int which)
                                {
                                    try
                                    {
                                        startActivity(new Intent(android.provider.Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
                                    }
                                    catch (Throwable ignored)
                                    {
                                    }
                                    gameEvents.offer(new String[] { "quit" });
                                }
                            })
                            .setNegativeButton("退出", new DialogInterface.OnClickListener()
                            {
                                public void onClick(DialogInterface dialog, int which)
                                {
                                    gameEvents.offer(new String[] { "quit" });
                                }
                            })
                            .show();
                        return;
                    }
                    String text = "这个安装包只包含手机移植部分，不含游戏本体。请导入你自己的 RimWorld 1.7：\n\n"
                        + "1. 在电脑的 Steam 库里右键 RimWorld → 管理 → 浏览本地文件，打开游戏文件夹；\n"
                        + "2. 把整个文件夹打包成 zip，传到手机上（或者直接把整个文件夹拷到手机上）；\n"
                        + "3. 点下面的按钮选择它。\n\n"
                        + "需要约 3 GB 空闲空间，复制和处理需要一两分钟。\n\n"
                        + "如果文件选择器打不开，或者一选就退出：用数据线把手机连到电脑，把整个游戏文件夹复制到手机存储的 Android/data/" + getPackageName()
                        + "/files/import 文件夹里（没有 import 就新建一个），再打开 Port17。";
                    if (reimporting)
                    {
                        text = "重新导入游戏：存档、设置和 mod 都会保留，只替换游戏本体。不想换了就点“退出”，下次打开照常用原来的游戏"
                            + "（已经开始处理过新文件的话，下次打开仍会请你选择）。\n\n" + text;
                    }
                    if (shownProblem != null && !shownProblem.isEmpty())
                    {
                        text = "上次没有成功：" + shownProblem + "\n\n" + text;
                    }
                    // The notice RimWorld's EULA asks of mods, and the way to the licenses of what the APK contains.
                    SpannableStringBuilder message = new SpannableStringBuilder(text);
                    message.append("\n\n本程序是 RimWorld 的非官方安卓移植，免费提供，与 Ludeon Studios 无关，也未经其认可。\n")
                        .append(LUDEON_NOTICE).append("\n\n");
                    int start = message.length();
                    message.append("关于与开源许可");
                    message.setSpan(new ClickableSpan()
                    {
                        @Override
                        public void onClick(View view)
                        {
                            showNotices();
                        }
                    }, start, message.length(), Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
                    AlertDialog dialog = askDialog = new AlertDialog.Builder(MainActivity.this)
                        .setTitle("导入你自己的 RimWorld")
                        .setMessage(message)
                        .setCancelable(false)
                        .setPositiveButton("选择压缩包", new DialogInterface.OnClickListener()
                        {
                            public void onClick(DialogInterface dialog, int which)
                            {
                                pickGame(false);
                            }
                        })
                        .setNeutralButton("选择文件夹", new DialogInterface.OnClickListener()
                        {
                            public void onClick(DialogInterface dialog, int which)
                            {
                                pickGame(true);
                            }
                        })
                        .setNegativeButton("退出", new DialogInterface.OnClickListener()
                        {
                            public void onClick(DialogInterface dialog, int which)
                            {
                                gameEvents.offer(new String[] { "quit" });
                            }
                        })
                        .show();
                    TextView shown = (TextView) dialog.findViewById(android.R.id.message);
                    if (shown != null)
                    {
                        shown.setMovementMethod(LinkMovementMethod.getInstance());
                    }
                }
            });
            String[] event = takeGameEvent();
            if ("quit".equals(event[0]))
            {
                return null;
            }
            if ("picked".equals(event[0]))
            {
                return event[1];
            }
            if ("failed".equals(event[0]))
            {
                problem = event[1];
            }
            // "again": the chooser was closed without a choice; the dialog comes back.
        }
    }

    private String[] takeGameEvent()
    {
        while (true)
        {
            try
            {
                return gameEvents.take();
            }
            catch (InterruptedException ignored)
            {
            }
        }
    }

    private void pickGame(boolean folder)
    {
        try
        {
            Intent intent;
            if (folder)
            {
                intent = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE);
            }
            else
            {
                intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
                intent.addCategory(Intent.CATEGORY_OPENABLE);
                intent.setType("*/*");
            }
            // Which app answers (on some phones the maker's own file manager), for the trail.
            String chooser = "?";
            try
            {
                android.content.pm.ResolveInfo answers = getPackageManager().resolveActivity(intent, 0);
                if (answers != null && answers.activityInfo != null)
                {
                    chooser = answers.activityInfo.packageName + "/" + answers.activityInfo.name;
                }
            }
            catch (Throwable ignored)
            {
            }
            Diagnostics.trail(this, "chooser opened for a " + (folder ? "folder" : "zip") + " (" + chooser + ")");
            startActivityForResult(intent, folder ? PICK_GAME_FOLDER : PICK_GAME_ZIP);
        }
        catch (Throwable problem)
        {
            gameEvents.offer(new String[] { "failed", "打不开文件选择器：" + problem });
        }
    }

    /** onActivityResult for the game's pick: copies what was chosen to files/import (on a thread of its own) and finds the game in it. */
    private void onGamePicked(final int request, int result, Intent data)
    {
        final Uri uri = result == RESULT_OK && data != null ? data.getData() : null;
        if (uri == null)
        {
            Diagnostics.trail(this, "chooser closed without a choice (result " + result + ")");
            gameEvents.offer(new String[] { "again" });
            return;
        }
        Diagnostics.trail(this, "chosen " + (request == PICK_GAME_FOLDER ? "folder " : "file ") + uri);
        final File ext = getExternalFilesDir(null);
        if (ext.getUsableSpace() < (3L << 30))
        {
            Diagnostics.trail(this, "not enough space: " + (ext.getUsableSpace() >> 20) + " MB");
            gameEvents.offer(new String[] { "failed", "手机上的空闲空间不够（需要约 3 GB，现在只有 " + (ext.getUsableSpace() >> 20) + " MB）。" });
            return;
        }
        gamePickRunning = true;
        if (askDialog != null && askDialog.isShowing())
        {
            // Shown again by a process the system started to take the chooser's answer; it is answered now.
            dismiss(askDialog);
        }
        showBusy("正在复制游戏文件…");
        new Thread(new Runnable()
        {
            @Override
            public void run()
            {
                final File into = new File(ext, "import");
                final boolean[] copying = { true };
                pickCount.set(0);
                pickBytes.set(0);
                Thread meter = new Thread(new Runnable()
                {
                    @Override
                    public void run()
                    {
                        while (copying[0])
                        {
                            updateBusy("正在复制游戏文件：" + pickCount.get() + " 个文件，" + (pickBytes.get() >> 20) + " MB\n\n"
                                + "可以切到别的应用，但不要在最近任务里把本应用划掉：那样复制会停止，下次打开要重新选择。");
                            try
                            {
                                Thread.sleep(500);
                            }
                            catch (InterruptedException ignored)
                            {
                            }
                        }
                    }
                });
                meter.start();
                try
                {
                    delete(into);
                    into.mkdirs();
                    if (request == PICK_GAME_FOLDER)
                    {
                        String id = DocumentsContract.getTreeDocumentId(uri);
                        File folder = new File(into, safeName(nameOf(DocumentsContract.buildDocumentUriUsingTree(uri, id), "RimWorld")));
                        folder.mkdirs();
                        copyTreeInParallel(uri, id, folder);
                    }
                    else
                    {
                        String name = nameOf(uri, "RimWorld.zip");
                        if (!name.toLowerCase().endsWith(".zip"))
                        {
                            throw new IOException("所选的不是 zip 压缩包（" + name + "）");
                        }
                        File folder = new File(into, safeName(name.substring(0, name.length() - 4)));
                        unpack(uri, folder);
                        if (pickCount.get() == 0)
                        {
                            // Not one entry where a stream reader looks for them (a tester's "RimWorld1.zip"): read again by
                            // the archive's directory at its end, as Windows and 7-Zip do.
                            String what = describeFile(uri);
                            String other = otherArchive(uri);
                            if (other != null)
                            {
                                // What the file really is (a tester's "RimWorld1.zip" was a Windows program, 815 MB).
                                Diagnostics.trail(MainActivity.this, name + " is not a zip (" + what + ")");
                                throw new IOException("选的“" + name + "”其实是" + other + "，只是扩展名改成了 zip，手机上打不开。请在电脑上把它解开，得到游戏文件夹以后，再把文件夹打包成 zip 传到手机上（" + what + "）");
                            }
                            Diagnostics.trail(MainActivity.this, "nothing read from " + name + " as a stream (" + what + "): by its directory");
                            try
                            {
                                unpackByDirectory(uri, folder, into);
                            }
                            catch (IOException again)
                            {
                                Diagnostics.trail(MainActivity.this, "by its directory: " + again);
                            }
                            if (pickCount.get() == 0)
                            {
                                throw new IOException("压缩包里一个文件也读不出来（" + what + "）。它可能不是 zip 格式（比如 7z、rar 改了扩展名），或者没有传完整。请在电脑上重新打包成 zip 再传到手机上");
                            }
                        }
                    }
                    copying[0] = false;
                    File root = findGame(into, 4);
                    hideBusy();
                    Diagnostics.trail(MainActivity.this, "copied " + pickCount.get() + " files, " + (pickBytes.get() >> 20) + " MB; the game " + (root != null ? "at " + root : "not found in: " + outline(into, 3)));
                    if (root == null)
                    {
                        delete(into);
                        gamePickRunning = false;
                        gameEvents.offer(new String[] { "failed", "所选的文件里没有找到 RimWorld 的游戏文件（应包含 Version.txt、Assembly-CSharp.dll、Data 文件夹等）。请选择 Steam 里 RimWorld 的整个游戏文件夹或它的压缩包。" });
                    }
                    else
                    {
                        gamePickRunning = false;
                        gameEvents.offer(new String[] { "picked", root.getPath() });
                    }
                }
                catch (Throwable problem)
                {
                    copying[0] = false;
                    hideBusy();
                    Diagnostics.trail(MainActivity.this, "copy failed after " + pickCount.get() + " files, " + (pickBytes.get() >> 20) + " MB: " + problem);
                    delete(into);
                    gamePickRunning = false;
                    gameEvents.offer(new String[] { "failed", "复制失败：" + (problem.getMessage() != null ? problem.getMessage() : problem.toString()) });
                }
            }
        }, "PortGameImport").start();
    }

    /** What a copied folder holds, a few levels down and a few names a level, for the import's trail. */
    private static String outline(File dir, int depth)
    {
        File[] children = dir.listFiles();
        if (children == null)
        {
            return dir.getName();
        }
        StringBuilder text = new StringBuilder(dir.getName()).append("/{");
        for (int i = 0; i < children.length && i < 12; i++)
        {
            text.append(i > 0 ? ", " : "").append(depth > 1 && children[i].isDirectory() ? outline(children[i], depth - 1) : children[i].getName());
        }
        return text.append(children.length > 12 ? ", … " + children.length + " in all}" : "}").toString();
    }

    /** The folder below dir (or dir itself) that holds the game: Version.txt and Assembly-CSharp.dll side by side. */
    private static File findGame(File dir, int depth)
    {
        if (new File(dir, "Version.txt").isFile() && new File(dir, "Assembly-CSharp.dll").isFile())
        {
            return dir;
        }
        File[] inside = depth > 0 ? dir.listFiles() : null;
        if (inside != null)
        {
            for (File child : inside)
            {
                if (child.isDirectory())
                {
                    File found = findGame(child, depth - 1);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
        }
        return null;
    }

    /**
     * Runs SetupService on root ("" for the kept assemblies) and waits for setup/result.txt: {state, message}. When the
     * runtime did not even start (-2, coreclr_initialize failed: a tester's Redmi Note 13 Pro, every time), once more in
     * a new process with safer runtime settings (host.c, runSetup's "safe").
     */
    private String[] runSetup(String root)
    {
        String[] result = runSetupOnce(root, false);
        if ("error".equals(result[0]) && result[1].startsWith("处理程序没能启动（-2"))
        {
            Diagnostics.trail(this, "the runtime did not start (" + result[1] + "): again with safer settings");
            // The first setup process ends itself after its result: the second one is started once it is gone.
            for (int i = 0; i < 20 && setupRunning(); i++)
            {
                try
                {
                    Thread.sleep(250);
                }
                catch (InterruptedException ignored)
                {
                }
            }
            String[] second = runSetupOnce(root, true);
            return "ok".equals(second[0]) ? second : new String[] { second[0], second[1] + "（第一次：" + result[1] + "）" };
        }
        return result;
    }

    /**
     * One run of SetupService. A setup process an earlier start of the app left running is ended first (a tester's vivo:
     * the app was swiped away and opened again while setup still ran, the new start reached the old process, whose
     * runtime was up already: 0x80131022). While it runs the setup process is bound as important to this activity, so
     * that the system schedules it as the app on the screen: on that vivo a setup process of its own was given the four
     * slow cores and stood minutes at the game's main assembly (eight seconds on the cores of the screen's app).
     */
    private String[] runSetupOnce(String root, boolean safe)
    {
        endEarlierSetup();
        android.content.ServiceConnection important = new android.content.ServiceConnection()
        {
            @Override
            public void onServiceConnected(ComponentName name, android.os.IBinder service)
            {
            }

            @Override
            public void onServiceDisconnected(ComponentName name)
            {
            }
        };
        boolean bound = false;
        try
        {
            // Without BIND_AUTO_CREATE: the connection comes when startService has created it, and a process that ends
            // itself after its work is not started again for the binding.
            bound = bindService(new Intent(this, SetupService.class), important, BIND_IMPORTANT);
        }
        catch (Throwable problem)
        {
            Diagnostics.trail(this, "setup not bound: " + problem);
        }
        try
        {
            return waitForSetup(root, safe);
        }
        finally
        {
            if (bound)
            {
                try
                {
                    unbindService(important);
                }
                catch (Throwable ignored)
                {
                }
            }
        }
    }

    /** Ends a setup process that is still there (from an earlier start of the app), and waits a little for it to go. */
    private void endEarlierSetup()
    {
        ActivityManager manager = (ActivityManager) getSystemService(ACTIVITY_SERVICE);
        List<ActivityManager.RunningAppProcessInfo> processes = manager != null ? manager.getRunningAppProcesses() : null;
        if (processes == null)
        {
            return;
        }
        for (ActivityManager.RunningAppProcessInfo process : processes)
        {
            if (process.processName.endsWith(":setup"))
            {
                Diagnostics.trail(this, "a setup process of an earlier start was still running (pid " + process.pid + "): ended");
                android.os.Process.killProcess(process.pid);
            }
        }
        for (int i = 0; i < 20 && setupRunning(); i++)
        {
            try
            {
                Thread.sleep(250);
            }
            catch (InterruptedException ignored)
            {
            }
        }
    }

    private String[] waitForSetup(String root, boolean safe)
    {
        String lastStep = "";
        long stepSince = System.currentTimeMillis();
        boolean stalledNoted = false;
        File ext = getExternalFilesDir(null);
        File resultFile = new File(ext, "setup/result.txt");
        File progressFile = new File(ext, "setup/progress.txt");
        resultFile.delete();
        progressFile.delete();
        showBusy(root.isEmpty() ? "正在按新版本重新处理游戏文件…" : "正在处理游戏文件…");
        Diagnostics.trail(this, "setup starts" + (root.isEmpty() ? " (the kept assemblies again)" : " on " + root) + (safe ? ", safer runtime settings" : ""));
        startService(new Intent(this, SetupService.class).putExtra("root", root).putExtra("safe", safe));
        long started = System.currentTimeMillis();
        long goneSince = 0;
        while (!resultFile.exists())
        {
            try
            {
                Thread.sleep(500);
            }
            catch (InterruptedException ignored)
            {
            }
            String step = readText(progressFile);
            if (!step.equals(lastStep))
            {
                lastStep = step;
                stepSince = System.currentTimeMillis();
                stalledNoted = false;
            }
            else if (!stalledNoted && System.currentTimeMillis() - stepSince > 120000)
            {
                // A step that does not move (a tester's vivo stood for minutes at the game's assemblies): in the trail, and the
                // player told what to do.
                stalledNoted = true;
                Diagnostics.trail(this, "setup stands at \"" + step + "\" for two minutes");
            }
            if (!step.isEmpty())
            {
                updateBusy("正在处理游戏文件：" + step + "\n\n请停留在这个界面，不要切出去或划掉本应用（会中断处理）。" + (stalledNoted ? "\n\n这一步比平常久了很多。如果再过几分钟还没有变化，请长按桌面上的 Port17 图标，选“导出日志”，把日志发给我们。" : ""));
            }
            // The setup process gone without a result (it crashed): given up a few seconds later.
            if (System.currentTimeMillis() - started > 5000 && !setupRunning())
            {
                if (goneSince == 0)
                {
                    goneSince = System.currentTimeMillis();
                }
                else if (System.currentTimeMillis() - goneSince > 3000 && !resultFile.exists())
                {
                    Diagnostics.trail(this, "the setup process is gone without a result (last step: " + readText(progressFile) + ")");
                    hideBusy();
                    return new String[] { "error", "处理程序意外退出了。" };
                }
            }
            else
            {
                goneSince = 0;
            }
        }
        hideBusy();
        String text = readText(resultFile);
        int newline = text.indexOf('\n');
        return newline < 0 ? new String[] { text.trim(), "" } : new String[] { text.substring(0, newline).trim(), text.substring(newline + 1).trim() };
    }

    private boolean setupRunning()
    {
        ActivityManager manager = (ActivityManager) getSystemService(ACTIVITY_SERVICE);
        List<ActivityManager.RunningAppProcessInfo> processes = manager != null ? manager.getRunningAppProcesses() : null;
        if (processes != null)
        {
            for (ActivityManager.RunningAppProcessInfo process : processes)
            {
                if (process.processName.endsWith(":setup"))
                {
                    return true;
                }
            }
        }
        return false;
    }

    static String readText(File file)
    {
        try
        {
            InputStream in = new java.io.FileInputStream(file);
            try
            {
                byte[] buffer = new byte[(int) Math.min(Math.max(file.length(), 1), 1 << 16)];
                int n = in.read(buffer);
                return n > 0 ? new String(buffer, 0, n, "UTF-8") : "";
            }
            finally
            {
                in.close();
            }
        }
        catch (IOException none)
        {
            return "";
        }
    }

    /** RimWorld's EULA: the notice a mod made with Ludeon's materials has to carry, word for word. */
    private static final String LUDEON_NOTICE = "Portions of the materials used to create this content/mod are trademarks and/or copyrighted works of Ludeon Studios Inc. "
        + "All rights reserved by Ludeon. This content/mod is not official and is not endorsed by Ludeon.";

    /** About and licenses: assets/licenses/NOTICE.txt (host/licenses), then the license texts themselves. On the UI thread. */
    private void showNotices()
    {
        new AlertDialog.Builder(this)
            .setTitle("关于与开源许可")
            .setMessage(readAsset("licenses/NOTICE.txt"))
            .setPositiveButton("关闭", null)
            .setNeutralButton("许可原文", new DialogInterface.OnClickListener()
            {
                public void onClick(DialogInterface dialog, int which)
                {
                    showLicenseList();
                }
            })
            .show();
    }

    private void showLicenseList()
    {
        List<String> names = new ArrayList<String>();
        try
        {
            for (String name : getAssets().list("licenses"))
            {
                if (!name.equals("NOTICE.txt"))
                {
                    names.add(name);
                }
            }
        }
        catch (IOException ignored)
        {
        }
        java.util.Collections.sort(names, String.CASE_INSENSITIVE_ORDER);
        final String[] items = names.toArray(new String[0]);
        new AlertDialog.Builder(this)
            .setTitle("许可原文")
            .setItems(items, new DialogInterface.OnClickListener()
            {
                public void onClick(DialogInterface dialog, int which)
                {
                    new AlertDialog.Builder(MainActivity.this)
                        .setTitle(items[which])
                        .setMessage(readAsset("licenses/" + items[which]))
                        .setPositiveButton("返回", new DialogInterface.OnClickListener()
                        {
                            public void onClick(DialogInterface dialog, int which)
                            {
                                showLicenseList();
                            }
                        })
                        .show();
                }
            })
            .setPositiveButton("关闭", null)
            .show();
    }

    private String readAsset(String name)
    {
        try
        {
            InputStream in = getAssets().open(name);
            try
            {
                java.io.ByteArrayOutputStream bytes = new java.io.ByteArrayOutputStream();
                byte[] buffer = new byte[16384];
                int n;
                while ((n = in.read(buffer)) > 0)
                {
                    bytes.write(buffer, 0, n);
                }
                return new String(bytes.toByteArray(), "UTF-8").replace("\r\n", "\n").replace("﻿", "");
            }
            finally
            {
                in.close();
            }
        }
        catch (IOException none)
        {
            return "（找不到 " + name + "）";
        }
    }

    private void showBusy(final String text)
    {
        runOnUiThread(new Runnable()
        {
            @Override
            public void run()
            {
                if (isFinishing() || isDestroyed())
                {
                    return;
                }
                if (busyDialog != null)
                {
                    busyDialog.setMessage(text);
                    return;
                }
                busyDialog = new AlertDialog.Builder(MainActivity.this).setTitle("请稍候").setMessage(text).setCancelable(false).show();
            }
        });
    }

    private void updateBusy(final String text)
    {
        runOnUiThread(new Runnable()
        {
            @Override
            public void run()
            {
                if (busyDialog != null)
                {
                    busyDialog.setMessage(text);
                }
            }
        });
    }

    private void hideBusy()
    {
        runOnUiThread(new Runnable()
        {
            @Override
            public void run()
            {
                if (busyDialog != null)
                {
                    dismiss(busyDialog);
                    busyDialog = null;
                }
            }
        });
    }

    /** Closes a dialog; nothing happens when its window is gone already (the activity went away). */
    private static void dismiss(Dialog dialog)
    {
        try
        {
            dialog.dismiss();
        }
        catch (Throwable ignored)
        {
        }
    }

    /** A message the player confirms; blocks the calling (non-UI) thread until then. */
    private void tell(final String title, final String text)
    {
        gameEvents.clear();
        runOnUiThread(new Runnable()
        {
            @Override
            public void run()
            {
                new AlertDialog.Builder(MainActivity.this).setTitle(title).setMessage(text).setCancelable(false)
                    .setPositiveButton("好的", new DialogInterface.OnClickListener()
                    {
                        public void onClick(DialogInterface dialog, int which)
                        {
                            gameEvents.offer(new String[] { "ok" });
                        }
                    })
                    .show();
            }
        });
        takeGameEvent();
    }

    private void toast(final String text)
    {
        runOnUiThread(new Runnable()
        {
            @Override
            public void run()
            {
                Toast.makeText(MainActivity.this, text, Toast.LENGTH_LONG).show();
            }
        });
    }

    private void leave()
    {
        runOnUiThread(new Runnable()
        {
            @Override
            public void run()
            {
                // The player's own choice: not an import cut off by the activity going away (onDestroy).
                importing = false;
                Diagnostics.noteSelfExit(MainActivity.this);
                finishAndRemoveTask();
                android.os.Process.killProcess(android.os.Process.myPid());
            }
        });
    }

    /**
     * Starts the game again in a new process; called from the native side (the GLFW shim's glfwAndroidRestart) when the
     * game wants to restart (the mod page's "save and apply", a change of language). The PC game launches its
     * executable again, which an app cannot do: the system is asked to start the activity's task afresh, and this
     * process ends at once (killed, not exited: the engine's threads are still running, and the C++ runtime's exit
     * handlers would pull things from under them).
     */
    public void restartGame(final boolean unused)
    {
        runOnUiThread(new Runnable()
        {
            @Override
            public void run()
            {
                Intent launch = getPackageManager().getLaunchIntentForPackage(getPackageName());
                ComponentName component = launch != null && launch.getComponent() != null ? launch.getComponent() : new ComponentName(MainActivity.this, MainActivity.class);
                Diagnostics.noteSelfExit(MainActivity.this);
                startActivity(Intent.makeRestartActivityTask(component));
                android.os.Process.killProcess(android.os.Process.myPid());
            }
        });
    }

    /**
     * The game quits; called from the native side (the GLFW shim's glfwAndroidPrepareExit) just before the process ends
     * by itself. The task goes with the activity, so that the game does not stay in the list of recent apps as a dead
     * entry.
     */
    public void finishGame(final boolean unused)
    {
        runOnUiThread(new Runnable()
        {
            @Override
            public void run()
            {
                finishAndRemoveTask();
            }
        });
    }

    /**
     * Whether showKeyboard forces the soft keyboard up (the default), or only gives the field to the input method, which
     * shows it if the player wants it with a hardware keyboard (set by the game: mobile/InputMode.cs, with a keyboard
     * plugged in). The input method still gets the keys of the hardware keyboard either way (pinyin on it, for one).
     */
    private volatile boolean softKeyboardForced = true;

    public void softKeyboardForced(boolean forced)
    {
        softKeyboardForced = forced;
    }

    /** Shows or hides the soft keyboard; called from the native side (the GLFW shim) when a text field takes or loses the focus. */
    public void showKeyboard(final boolean show)
    {
        final boolean forced = softKeyboardForced;
        runOnUiThread(new Runnable()
        {
            @Override
            public void run()
            {
                InputMethodManager imm = (InputMethodManager) getSystemService(INPUT_METHOD_SERVICE);
                if (imm == null)
                {
                    return;
                }
                if (show)
                {
                    boolean was = textInput.isActive();
                    textInput.setActive(true);
                    // Android does not start the input method afresh when the view that lost the focus takes it again:
                    // the input method keeps what it was given. When its process was restarted meanwhile (the system kills
                    // it for memory while the game runs), it was given this window without an editor, and kept that:
                    // everything typed went nowhere until a floating window or a screen recording took the window's focus
                    // away and back (2026-10-24: Chinese swallowed after playing a while; Baidu IME killed by hand repeats it). So a field that takes
                    // the focus, or a keyboard asked for again while the input method has no editor, starts it afresh.
                    if (!was || !imm.isAcceptingText())
                    {
                        imm.restartInput(textInput);
                    }
                    if (!forced)
                    {
                        imm.showSoftInput(textInput, 0);
                    }
                    else if (!imm.showSoftInput(textInput, InputMethodManager.SHOW_FORCED))
                    {
                        imm.toggleSoftInput(InputMethodManager.SHOW_FORCED, 0);
                    }
                }
                else
                {
                    imm.hideSoftInputFromWindow(textInput.getWindowToken(), 0);
                    textInput.setActive(false);
                }
            }
        });
    }

    @SuppressWarnings("deprecation")
    private void hideSystemBars()
    {
        // The pre-30 flags still work on current Android and keep this a one-liner; sticky immersive brings the bars
        // back only transiently on a swipe from the edge.
        getWindow().getDecorView().setSystemUiVisibility(
            View.SYSTEM_UI_FLAG_LAYOUT_STABLE
            | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
            | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
            | View.SYSTEM_UI_FLAG_FULLSCREEN
            | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
    }
}

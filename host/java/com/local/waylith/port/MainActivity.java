package com.local.waylith.port;

import android.app.NativeActivity;
import android.content.ClipData;
import android.content.ComponentName;
import android.content.ContentResolver;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.DocumentsContract;
import android.provider.OpenableColumns;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;

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
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        // The view the soft keyboard talks to (TextInputView), one pixel in the corner; the game's own view keeps the touches.
        textInput = new TextInputView(this);
        addContentView(textInput, new ViewGroup.LayoutParams(1, 1));
        hideSystemBars();
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
     * threads at once. The first failure stops the rest and is reported.
     */
    private void copyTreeInParallel(Uri tree, String documentId, File into) throws IOException
    {
        ExecutorService pool = Executors.newFixedThreadPool(COPY_THREADS);
        AtomicReference<Throwable> failure = new AtomicReference<Throwable>();
        try
        {
            copyTree(tree, documentId, into, pool, failure);
        }
        finally
        {
            pool.shutdown();
        }
        try
        {
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
        Throwable problem = failure.get();
        if (problem != null)
        {
            throw problem instanceof IOException ? (IOException)problem : new IOException(String.valueOf(problem));
        }
    }

    private static final int COPY_THREADS = 8;

    private void copyTree(final Uri tree, String documentId, File into, ExecutorService pool, final AtomicReference<Throwable> failure) throws IOException
    {
        ContentResolver resolver = getContentResolver();
        Uri children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, documentId);
        Cursor cursor = resolver.query(children, new String[] { DocumentsContract.Document.COLUMN_DOCUMENT_ID,
            DocumentsContract.Document.COLUMN_DISPLAY_NAME, DocumentsContract.Document.COLUMN_MIME_TYPE }, null, null, null);
        if (cursor == null)
        {
            throw new IOException("cannot list " + documentId);
        }
        try
        {
            while (cursor.moveToNext())
            {
                String id = cursor.getString(0);
                File out = new File(into, safeName(cursor.getString(1)));
                if (failure.get() != null)
                {
                    return;
                }
                if (DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(2)))
                {
                    out.mkdirs();
                    copyTree(tree, id, out, pool, failure);
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
                                InputStream in = getContentResolver().openInputStream(DocumentsContract.buildDocumentUriUsingTree(tree, fileId));
                                if (in == null)
                                {
                                    throw new IOException("cannot open " + fileId);
                                }
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
        finally
        {
            cursor.close();
        }
    }

    // Archives made on a Chinese Windows often carry file names in GBK without saying so; read as UTF-8 such a
    // name is rejected, and the archive is read again from the start as GBK.
    private void unpack(Uri uri, File into) throws IOException
    {
        try
        {
            unpack(uri, into, Charset.forName("UTF-8"));
        }
        catch (IllegalArgumentException malformedName)
        {
            delete(into);
            unpack(uri, into, Charset.forName("GBK"));
        }
    }

    private void unpack(Uri uri, File into, Charset names) throws IOException
    {
        InputStream raw = getContentResolver().openInputStream(uri);
        if (raw == null)
        {
            throw new IOException("cannot open " + uri);
        }
        ZipInputStream zip = new ZipInputStream(raw, names);
        try
        {
            into.mkdirs();
            String base = into.getCanonicalPath() + File.separator;
            ZipEntry entry;
            while ((entry = zip.getNextEntry()) != null)
            {
                File out = new File(into, entry.getName().replace('\\', '/'));
                // An entry must not climb out of the folder it is unpacked into.
                if (!out.getCanonicalPath().startsWith(base))
                {
                    continue;
                }
                if (entry.isDirectory() || entry.getName().endsWith("/") || entry.getName().endsWith("\\"))
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

    /** Shows or hides the soft keyboard; called from the native side (the GLFW shim) when a text field takes or loses the focus. */
    public void showKeyboard(final boolean show)
    {
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
                    textInput.setActive(true);
                    if (!imm.showSoftInput(textInput, InputMethodManager.SHOW_FORCED))
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

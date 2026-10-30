package com.local.waylith.port;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.Bundle;
import android.os.CancellationSignal;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.ParcelFileDescriptor;
import android.provider.DocumentsContract;
import android.provider.DocumentsContract.Document;
import android.provider.DocumentsContract.Root;
import android.provider.DocumentsProvider;
import android.util.Log;
import android.webkit.MimeTypeMap;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.StandardCopyOption;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.UUID;

/**
 * The game's data folder (Config with the mods' settings, Saves, ModLists, Scenarios, Ideos, Xenotypes, Logs...) and
 * its Mods folder as two places of the system's Files app (DocumentsUI) and of every app's file chooser, where the PC
 * has Explorer. Both lie under Android/data, which no file manager opens since Android 11: mod settings, mod lists and
 * what mods write there went in and out only by cable (2026-10-30, from comparing with the PC), and what a file manager
 * copied in belonged to that app, which the game may not read (hooks/ModFolders.cs and mobile/SaveImport.cs leave such
 * files out). What comes in through this provider is the app's own.
 *
 * Nothing else is reachable. A document id is a place ("data", "mods"), or a place and a path below its folder
 * ("data/Saves/Colony.rws"): every name in it one a folder can hold (no "", ".", ".." or NUL), and where the path leads
 * once links are followed still inside the place (fileOf); links found in a listing are left out, never followed.
 *
 * What a client writes goes into a temporary file beside the target, which takes the target's place with one rename
 * only once the client has closed it without an error: a copy broken off, or an app that dies while writing, leaves the
 * file as it was, and the game never reads half of one. The provider runs in a process of its own (":files" in the
 * manifest): the game ending itself (the restart after a change of mods) or crashing neither loses a write that is
 * not in place yet nor ends the app that has a file of it open (the system ends a client with a stable connection to a
 * provider whose process dies, see DocumentReader).
 */
public final class FilesProvider extends DocumentsProvider
{
    private static final String TAG = "port17";

    /** The two places by root id, which is also the document id of the place's folder. */
    static final String DATA = "data", MODS = "mods";
    private static final String[] PLACES = { DATA, MODS };

    /** The beginning of the names of the temporary files writes go into: never listed or copied. */
    static final String TEMP = ".port17-write-";
    /** A temporary file this old that no client writes into is what a crash left behind, and is removed. */
    private static final long STALE = 24L * 60 * 60 * 1000;

    private static final String[] ROOT_COLUMNS = {
        Root.COLUMN_ROOT_ID, Root.COLUMN_FLAGS, Root.COLUMN_ICON, Root.COLUMN_TITLE, Root.COLUMN_SUMMARY,
        Root.COLUMN_DOCUMENT_ID, Root.COLUMN_AVAILABLE_BYTES };
    private static final String[] DOCUMENT_COLUMNS = {
        Document.COLUMN_DOCUMENT_ID, Document.COLUMN_MIME_TYPE, Document.COLUMN_DISPLAY_NAME,
        Document.COLUMN_LAST_MODIFIED, Document.COLUMN_FLAGS, Document.COLUMN_SIZE };

    /** The temporary files clients are writing into now (their paths). */
    private static final Set<String> writing = new HashSet<String>();

    private volatile File ext;
    private Handler closes;

    @Override
    public boolean onCreate()
    {
        return true;
    }

    /** The provider's authority, named after the package: host/build.sh gives a TEST_PACKAGE build its own. */
    static String authority(Context context)
    {
        return context.getPackageName() + ".files";
    }

    /** A place's folder below the app's external files folder: the game's data folder (where the host's HOME has it) or its Mods. */
    static File folderOf(File ext, String place)
    {
        if (DATA.equals(place))
        {
            return new File(ext, "home/unity3d/Ludeon Studios/RimWorld by Ludeon Studios");
        }
        if (MODS.equals(place))
        {
            return new File(ext, "game/Mods");
        }
        return null;
    }

    /** What the Files app calls a place. */
    private static String titleOf(String place)
    {
        return DATA.equals(place) ? "RimWorld 存档和设置" : "RimWorld 的 Mod";
    }

    /**
     * The file of a document id, which must be a place or a path below a place's folder: every name in the path one a
     * folder holds, and where the path leads once links are followed still inside the place's folder.
     */
    static File fileOf(File ext, String documentId) throws FileNotFoundException
    {
        int slash = documentId != null ? documentId.indexOf('/') : -1;
        File folder = documentId != null ? folderOf(ext, slash < 0 ? documentId : documentId.substring(0, slash)) : null;
        if (folder == null)
        {
            throw new FileNotFoundException("not a place of the game's: " + documentId);
        }
        if (slash < 0)
        {
            return folder;
        }
        File file = folder;
        for (String name : documentId.substring(slash + 1).split("/", -1))
        {
            if (name.isEmpty() || name.equals(".") || name.equals("..") || name.indexOf('\0') >= 0)
            {
                throw new FileNotFoundException("not a path in the game's folders: " + documentId);
            }
            file = new File(file, name);
        }
        if (!below(folder, file))
        {
            throw new FileNotFoundException("outside the game's folders: " + documentId);
        }
        return file;
    }

    /** Whether a file lies below a folder once links are followed (for a missing file: where its path leads). */
    private static boolean below(File folder, File file)
    {
        try
        {
            return file.getCanonicalPath().startsWith(folder.getCanonicalPath() + File.separator);
        }
        catch (IOException e)
        {
            return false;
        }
    }

    /** The document id of a folder or file of one of the places, by the path the game uses, or null for any other path. */
    static String documentIdOf(File ext, File path)
    {
        try
        {
            String target = path.getCanonicalPath();
            for (String place : PLACES)
            {
                String base = folderOf(ext, place).getCanonicalPath();
                if (target.equals(base))
                {
                    return place;
                }
                if (target.startsWith(base + File.separator))
                {
                    return place + "/" + target.substring(base.length() + 1).replace(File.separatorChar, '/');
                }
            }
        }
        catch (IOException ignored)
        {
        }
        return null;
    }

    /** The folder above a document (a place for a file directly in its folder). */
    private static String parentOf(String documentId)
    {
        return documentId.substring(0, documentId.lastIndexOf('/'));
    }

    /**
     * The ids from the start (a place, or the folder a client was given) down to a document, which DocumentsUI asks for
     * to open a folder it was sent to (folderIntent) with the folders above it; null when the document is not below the
     * start.
     */
    static List<String> pathDown(String start, String documentId)
    {
        if (!documentId.equals(start) && !documentId.startsWith(start + "/"))
        {
            return null;
        }
        List<String> path = new ArrayList<String>();
        path.add(start);
        for (int slash = documentId.indexOf('/', start.length() + 1); slash > 0; slash = documentId.indexOf('/', slash + 1))
        {
            path.add(documentId.substring(0, slash));
        }
        if (!documentId.equals(start))
        {
            path.add(documentId);
        }
        return path;
    }

    private File ext() throws FileNotFoundException
    {
        if (ext == null)
        {
            ext = getContext().getExternalFilesDir(null);
            if (ext == null)
            {
                throw new FileNotFoundException("the phone's storage is not there");
            }
        }
        return ext;
    }

    private File fileOf(String documentId) throws FileNotFoundException
    {
        return fileOf(ext(), documentId);
    }

    // ---- what the Files app and the choosers ask ----

    @Override
    public Cursor queryRoots(String[] projection) throws FileNotFoundException
    {
        Context context = getContext();
        MatrixCursor roots = new MatrixCursor(projection != null ? projection : ROOT_COLUMNS);
        ApplicationInfo app = context.getApplicationInfo();
        // The app's label tells the side-by-side test package's places from the game's ("Port17 测试").
        String label = app.loadLabel(context.getPackageManager()).toString();
        for (String place : PLACES)
        {
            roots.newRow()
                .add(Root.COLUMN_ROOT_ID, place)
                .add(Root.COLUMN_FLAGS, Root.FLAG_SUPPORTS_CREATE | Root.FLAG_SUPPORTS_IS_CHILD | Root.FLAG_LOCAL_ONLY)
                .add(Root.COLUMN_ICON, app.icon != 0 ? app.icon : android.R.drawable.sym_def_app_icon)
                .add(Root.COLUMN_TITLE, titleOf(place))
                .add(Root.COLUMN_SUMMARY, label)
                .add(Root.COLUMN_DOCUMENT_ID, place)
                .add(Root.COLUMN_AVAILABLE_BYTES, ext().getUsableSpace());
        }
        roots.setNotificationUri(context.getContentResolver(), DocumentsContract.buildRootsUri(authority(context)));
        return roots;
    }

    @Override
    public Cursor queryDocument(String documentId, String[] projection) throws FileNotFoundException
    {
        MatrixCursor result = new MatrixCursor(projection != null ? projection : DOCUMENT_COLUMNS);
        File file = fileOf(documentId);
        BasicFileAttributes attributes = attributes(file);
        // A place whose folder the game has not made yet is shown all the same, empty.
        if (attributes == null && documentId.indexOf('/') >= 0)
        {
            throw new FileNotFoundException(documentId + " is not there");
        }
        addRow(result, documentId, file, attributes);
        return result;
    }

    @Override
    public Cursor queryChildDocuments(String parentDocumentId, String[] projection, String sortOrder) throws FileNotFoundException
    {
        File folder = fileOf(parentDocumentId);
        MatrixCursor result = new MatrixCursor(projection != null ? projection : DOCUMENT_COLUMNS);
        BasicFileAttributes attributes = attributes(folder);
        if (attributes == null ? parentDocumentId.indexOf('/') >= 0 : !attributes.isDirectory())
        {
            throw new FileNotFoundException(parentDocumentId + " is not a folder");
        }
        File[] files = attributes != null ? folder.listFiles() : null;
        if (files == null)
        {
            Bundle extras = new Bundle();
            if (attributes == null)
            {
                extras.putString(DocumentsContract.EXTRA_INFO, "游戏还没有建这个文件夹（还没启动过游戏）。往这里复制文件时会先建好它。");
            }
            else
            {
                // What a file manager copied in belongs to that app (the "Harmeny" folder a tester put into Mods, 2026-10-22).
                extras.putString(DocumentsContract.EXTRA_ERROR, "这个文件夹是别的应用（比如文件管理器）放进来的，属于那个应用，Port17 没有权限读。请用那个应用删掉它，再从这里重新复制进来。");
            }
            result.setExtras(extras);
        }
        else
        {
            for (File file : files)
            {
                String name = file.getName();
                if (name.startsWith(TEMP))
                {
                    dropIfStale(file);
                    continue;
                }
                BasicFileAttributes child = attributes(file);
                if (child != null && !child.isSymbolicLink())
                {
                    addRow(result, parentDocumentId + "/" + name, file, child);
                }
            }
        }
        Context context = getContext();
        result.setNotificationUri(context.getContentResolver(), DocumentsContract.buildChildDocumentsUri(authority(context), parentDocumentId));
        return result;
    }

    private static void addRow(MatrixCursor cursor, String documentId, File file, BasicFileAttributes attributes)
    {
        boolean place = documentId.indexOf('/') < 0;
        boolean folder = place || attributes.isDirectory();
        // The places' folders themselves stay where they are, with their names.
        int flags = Document.FLAG_DIR_SUPPORTS_CREATE;
        if (!place)
        {
            flags = Document.FLAG_SUPPORTS_DELETE | Document.FLAG_SUPPORTS_RENAME | Document.FLAG_SUPPORTS_COPY | Document.FLAG_SUPPORTS_MOVE
                | (folder ? Document.FLAG_DIR_SUPPORTS_CREATE : Document.FLAG_SUPPORTS_WRITE);
        }
        cursor.newRow()
            .add(Document.COLUMN_DOCUMENT_ID, documentId)
            .add(Document.COLUMN_MIME_TYPE, folder ? Document.MIME_TYPE_DIR : typeOf(file.getName()))
            .add(Document.COLUMN_DISPLAY_NAME, place ? titleOf(documentId) : file.getName())
            .add(Document.COLUMN_LAST_MODIFIED, attributes != null ? attributes.lastModifiedTime().toMillis() : null)
            .add(Document.COLUMN_FLAGS, flags)
            .add(Document.COLUMN_SIZE, folder ? null : attributes.size());
    }

    /** A file's type by its extension. The game's own files are XML: saves, scenarios, mod lists, ideoligions, xenotypes... */
    private static String typeOf(String name)
    {
        int dot = name.lastIndexOf('.');
        String extension = dot >= 0 ? name.substring(dot + 1).toLowerCase(Locale.ROOT) : "";
        switch (extension)
        {
            case "rws":
            case "rsc":
            case "rml":
            case "rid":
            case "xtp":
            case "xgm":
            case "ccg":
            case "rwh":
                return "text/xml";
            case "log":
                return "text/plain";
            default:
                String type = extension.isEmpty() ? null : MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension);
                return type != null ? type : "application/octet-stream";
        }
    }

    @Override
    public boolean isChildDocument(String parentDocumentId, String documentId)
    {
        try
        {
            fileOf(parentDocumentId);
            fileOf(documentId);
        }
        catch (FileNotFoundException e)
        {
            return false;
        }
        return documentId.startsWith(parentDocumentId + "/");
    }

    @Override
    public DocumentsContract.Path findDocumentPath(String parentDocumentId, String childDocumentId) throws FileNotFoundException
    {
        fileOf(childDocumentId);
        String place = childDocumentId.indexOf('/') < 0 ? childDocumentId : childDocumentId.substring(0, childDocumentId.indexOf('/'));
        List<String> path = pathDown(parentDocumentId != null ? parentDocumentId : place, childDocumentId);
        if (path == null)
        {
            throw new FileNotFoundException(childDocumentId + " is not in " + parentDocumentId);
        }
        // Below a folder a client was given, the path starts there and names no root (DocumentsProvider checks that).
        return new DocumentsContract.Path(parentDocumentId != null ? null : place, path);
    }

    // ---- reading and writing ----

    @Override
    public ParcelFileDescriptor openDocument(String documentId, String mode, CancellationSignal signal) throws FileNotFoundException
    {
        File file = fileOf(documentId);
        if (file.isDirectory())
        {
            throw new FileNotFoundException(documentId + " is a folder");
        }
        if (!mode.equals("r") && !mode.equals("w") && !mode.equals("wt") && !mode.equals("wa") && !mode.equals("rw") && !mode.equals("rwt"))
        {
            throw new IllegalArgumentException("Bad mode: " + mode);
        }
        if (mode.equals("r"))
        {
            return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY);
        }
        return openForWriting(documentId, file, mode);
    }

    /**
     * The client writes into a temporary file beside the target, which replaces the target when the client closes it
     * without an error. A client that reads what is there as well ("rw") or adds to it ("wa") gets a copy of it to work
     * on. "w" is taken as "wt": since Android 10 "w" no longer truncates, and an app writing a shorter file anew would
     * leave the old file's end behind.
     */
    private ParcelFileDescriptor openForWriting(final String documentId, final File file, String mode) throws FileNotFoundException
    {
        File folder = file.getParentFile();
        if (folder == null || !folder.isDirectory())
        {
            throw new FileNotFoundException("no folder for " + documentId);
        }
        final File temp = tempBeside(file);
        try
        {
            if ((mode.equals("rw") || mode.equals("wa")) && file.isFile())
            {
                copyFile(file, temp);
                // A client that only reads leaves the file's time as it was (the load window lists saves by it).
                temp.setLastModified(file.lastModified());
            }
            int flags = (mode.startsWith("rw") ? ParcelFileDescriptor.MODE_READ_WRITE : ParcelFileDescriptor.MODE_WRITE_ONLY)
                | ParcelFileDescriptor.MODE_CREATE | (mode.equals("wa") ? ParcelFileDescriptor.MODE_APPEND : 0);
            synchronized (writing)
            {
                writing.add(temp.getPath());
            }
            return ParcelFileDescriptor.open(temp, flags, closes(), new ParcelFileDescriptor.OnCloseListener()
            {
                @Override
                public void onClose(IOException problem)
                {
                    closed(documentId, file, temp, problem);
                }
            });
        }
        catch (IOException e)
        {
            synchronized (writing)
            {
                writing.remove(temp.getPath());
            }
            temp.delete();
            throw new FileNotFoundException("cannot write " + documentId + ": " + e.getMessage());
        }
    }

    /** The client is done with a file it wrote (on the closes thread): what it wrote takes the old file's place, or is thrown away. */
    private void closed(String documentId, File file, File temp, IOException problem)
    {
        try
        {
            // Only a client that died ("Remote side is dead") or closed with an error (closeWithError: DocumentsUI's copy that
            // failed) broke the write off. One that took the descriptor over (detachFd, for native code) closes it where no
            // one hears: the new file takes the old one's place at once, and the client goes on writing into it there. One
            // that closed only the descriptor inside, not this object, was done with it when the object is collected
            // ("Remote side was leaked").
            boolean done = problem == null || problem instanceof ParcelFileDescriptor.FileDescriptorDetachedException
                || String.valueOf(problem.getMessage()).contains("leaked");
            if (done && putInPlace(temp, file))
            {
                Log.i(TAG, "files: " + documentId + " written (" + file.length() + " bytes)");
                changed(parentOf(documentId));
            }
            else
            {
                temp.delete();
                Log.w(TAG, "files: " + documentId + " left as it was, the write was " + (done ? "not put in place" : "broken off: " + problem.getMessage()));
            }
        }
        catch (Throwable t)
        {
            Log.w(TAG, "files: " + documentId + " after writing", t);
        }
        finally
        {
            synchronized (writing)
            {
                writing.remove(temp.getPath());
            }
        }
    }

    /** The thread the ends of writes are heard on (ParcelFileDescriptor's close listener needs a looper). */
    private synchronized Handler closes()
    {
        if (closes == null)
        {
            HandlerThread thread = new HandlerThread("FilesProvider");
            thread.start();
            closes = new Handler(thread.getLooper());
        }
        return closes;
    }

    /** A temporary file for a write, beside the target (so the rename stays on its storage), under a short name of its own. */
    static File tempBeside(File file)
    {
        return new File(file.getParentFile(), TEMP + UUID.randomUUID().toString().replace("-", "").substring(0, 16) + ".tmp");
    }

    /**
     * A written file takes the target's place: flushed to the storage first, so that a phone losing power just after does
     * not find an empty file there, then moved there in one step, which replaces the old file at once (a rename on the
     * phone). Until then the old file is there as it was.
     */
    static boolean putInPlace(File temp, File file)
    {
        try (FileOutputStream out = new FileOutputStream(temp, true))
        {
            out.getFD().sync();
        }
        catch (IOException ignored)
        {
            // Written all the same, only perhaps not on the storage yet.
        }
        try
        {
            Files.move(temp.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE);
            return true;
        }
        catch (IOException | RuntimeException e)
        {
            return false;
        }
    }

    /** A temporary file left behind by a crash (not written into now, a day old) is removed when its folder is listed. */
    private static void dropIfStale(File temp)
    {
        synchronized (writing)
        {
            if (writing.contains(temp.getPath()))
            {
                return;
            }
        }
        if (System.currentTimeMillis() - temp.lastModified() > STALE && temp.delete())
        {
            Log.i(TAG, "files: removed " + temp + ", left from a write that was never finished");
        }
    }

    // ---- making, renaming, deleting, copying and moving ----

    /** The folder something goes into; a place's folder the game has not made yet is made now. */
    private File folderFor(String documentId) throws FileNotFoundException
    {
        File folder = fileOf(documentId);
        if (documentId.indexOf('/') < 0)
        {
            folder.mkdirs();
        }
        if (!folder.isDirectory())
        {
            throw new FileNotFoundException(documentId + " is not a folder");
        }
        return folder;
    }

    @Override
    public String createDocument(String parentDocumentId, String mimeType, String displayName) throws FileNotFoundException
    {
        File folder = folderFor(parentDocumentId);
        boolean isFolder = Document.MIME_TYPE_DIR.equals(mimeType);
        File file = freeName(folder, validName(displayName), isFolder);
        boolean made;
        try
        {
            made = isFolder ? file.mkdir() : file.createNewFile();
        }
        catch (IOException e)
        {
            made = false;
        }
        if (!made)
        {
            throw new IllegalStateException("could not make " + file.getName() + " in " + parentDocumentId);
        }
        String documentId = parentDocumentId + "/" + file.getName();
        Log.i(TAG, "files: made " + documentId);
        changed(parentDocumentId);
        return documentId;
    }

    @Override
    public String renameDocument(String documentId, String displayName) throws FileNotFoundException
    {
        File file = fileOf(documentId);
        if (documentId.indexOf('/') < 0)
        {
            throw new UnsupportedOperationException("a place keeps its name");
        }
        BasicFileAttributes attributes = attributes(file);
        if (attributes == null)
        {
            throw new FileNotFoundException(documentId + " is not there");
        }
        String name = validName(displayName);
        if (name.equals(file.getName()))
        {
            return null;
        }
        File target = new File(file.getParentFile(), name);
        boolean caseOnly = exists(target) && sameFile(file, target);
        if (!caseOnly)
        {
            target = freeName(file.getParentFile(), name, attributes.isDirectory());
        }
        // The phone's storage does not tell names apart by case, and a rename to the same name in other letters does
        // nothing there: it goes by way of another name (not a temporary file's, which a crash in between would lose).
        File step = caseOnly ? new File(file.getParentFile(), file.getName() + ".port17-renaming") : file;
        if (caseOnly && !file.renameTo(step))
        {
            throw new IllegalStateException("could not rename " + documentId);
        }
        if (!step.renameTo(target))
        {
            if (caseOnly)
            {
                step.renameTo(file);
            }
            throw new IllegalStateException("could not rename " + documentId);
        }
        String parent = parentOf(documentId);
        Log.i(TAG, "files: renamed " + documentId + " to " + target.getName());
        changed(parent);
        return parent + "/" + target.getName();
    }

    @Override
    public void deleteDocument(String documentId) throws FileNotFoundException
    {
        File file = fileOf(documentId);
        if (documentId.indexOf('/') < 0)
        {
            throw new UnsupportedOperationException("a place cannot be deleted");
        }
        if (!exists(file))
        {
            throw new FileNotFoundException(documentId + " is not there");
        }
        boolean all = deleteAll(file);
        changed(parentOf(documentId));
        if (!all)
        {
            throw new IllegalStateException("could not delete all of " + documentId);
        }
        Log.i(TAG, "files: deleted " + documentId);
    }

    @Override
    public String copyDocument(String sourceDocumentId, String targetParentDocumentId) throws FileNotFoundException
    {
        File source = fileOf(sourceDocumentId);
        if (sourceDocumentId.indexOf('/') < 0)
        {
            throw new UnsupportedOperationException("a place is not copied whole");
        }
        BasicFileAttributes attributes = attributes(source);
        if (attributes == null)
        {
            throw new FileNotFoundException(sourceDocumentId + " is not there");
        }
        File folder = folderFor(targetParentDocumentId);
        if (within(folder, source))
        {
            throw new IllegalArgumentException("a folder cannot be copied into itself");
        }
        File target = freeName(folder, source.getName(), attributes.isDirectory());
        try
        {
            copyAll(source, target);
        }
        catch (IOException e)
        {
            deleteAll(target);
            throw new IllegalStateException("could not copy " + sourceDocumentId + ": " + e.getMessage());
        }
        finally
        {
            changed(targetParentDocumentId);
        }
        String documentId = targetParentDocumentId + "/" + target.getName();
        Log.i(TAG, "files: copied " + sourceDocumentId + " to " + documentId);
        return documentId;
    }

    @Override
    public String moveDocument(String sourceDocumentId, String sourceParentDocumentId, String targetParentDocumentId) throws FileNotFoundException
    {
        File source = fileOf(sourceDocumentId);
        if (sourceDocumentId.indexOf('/') < 0)
        {
            throw new UnsupportedOperationException("a place stays where it is");
        }
        BasicFileAttributes attributes = attributes(source);
        if (attributes == null)
        {
            throw new FileNotFoundException(sourceDocumentId + " is not there");
        }
        File folder = folderFor(targetParentDocumentId);
        if (sameFile(folder, source.getParentFile()))
        {
            return sourceDocumentId;
        }
        if (within(folder, source))
        {
            throw new IllegalArgumentException("a folder cannot be moved into itself");
        }
        File target = freeName(folder, source.getName(), attributes.isDirectory());
        // Both places are on the same storage, where a rename moves anything at once; a copy and a delete otherwise.
        if (!source.renameTo(target))
        {
            try
            {
                copyAll(source, target);
            }
            catch (IOException e)
            {
                deleteAll(target);
                throw new IllegalStateException("could not move " + sourceDocumentId + ": " + e.getMessage());
            }
            deleteAll(source);
        }
        String documentId = targetParentDocumentId + "/" + target.getName();
        Log.i(TAG, "files: moved " + sourceDocumentId + " to " + documentId);
        changed(parentOf(sourceDocumentId));
        changed(targetParentDocumentId);
        return documentId;
    }

    /** DocumentsUI's lists of a folder and the folder's own entry are read again. */
    private void changed(String folderDocumentId)
    {
        Context context = getContext();
        String authority = authority(context);
        context.getContentResolver().notifyChange(DocumentsContract.buildChildDocumentsUri(authority, folderDocumentId), null, 0);
        context.getContentResolver().notifyChange(DocumentsContract.buildDocumentUri(authority, folderDocumentId), null, 0);
    }

    // ---- files ----

    /** A file's or a folder's attributes, of a link itself rather than what it leads to; null when there is nothing. */
    static BasicFileAttributes attributes(File file)
    {
        try
        {
            return Files.readAttributes(file.toPath(), BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS);
        }
        catch (IOException | RuntimeException e)
        {
            return null;
        }
    }

    private static boolean exists(File file)
    {
        return attributes(file) != null;
    }

    private static boolean sameFile(File one, File other)
    {
        try
        {
            return other != null && Files.isSameFile(one.toPath(), other.toPath());
        }
        catch (IOException | RuntimeException e)
        {
            return false;
        }
    }

    /** Whether a folder is the source or lies inside it, found by the files themselves (names may differ in case). */
    static boolean within(File folder, File source)
    {
        for (File up = folder; up != null; up = up.getParentFile())
        {
            if (sameFile(up, source))
            {
                return true;
            }
        }
        return false;
    }

    /** A name as a player or an app gave it, made one a folder can hold: no separators, control characters, nor a temporary file's name. */
    static String validName(String displayName)
    {
        if (displayName == null)
        {
            throw new IllegalArgumentException("no name");
        }
        StringBuilder name = new StringBuilder(displayName.length());
        for (int i = 0; i < displayName.length(); i++)
        {
            char c = displayName.charAt(i);
            // A backslash too: Windows has none in names, and the port's start reads one as a separator (MoveBackslashNames).
            name.append(c == '/' || c == '\\' || c < ' ' || c == 0x7f ? '_' : c);
        }
        String result = name.toString().trim();
        if (result.startsWith(TEMP))
        {
            result = "_" + result.substring(1);
        }
        if (result.isEmpty() || result.equals(".") || result.equals(".."))
        {
            throw new IllegalArgumentException("not a name: " + displayName);
        }
        return result;
    }

    /** A name not taken in the folder: the name itself, else with " (1)", " (2)"... before a file's extension, as the system's own provider does. */
    static File freeName(File folder, String name, boolean isFolder)
    {
        int dot = isFolder ? -1 : name.lastIndexOf('.');
        String stem = dot > 0 ? name.substring(0, dot) : name;
        String extension = dot > 0 ? name.substring(dot) : "";
        File file = new File(folder, name);
        for (int n = 1; exists(file); n++)
        {
            if (n > 999)
            {
                throw new IllegalStateException("no free name for " + name + " in " + folder);
            }
            file = new File(folder, stem + " (" + n + ")" + extension);
        }
        return file;
    }

    /** A file, or a folder with all it holds; a link is removed itself, never what it leads to. True when nothing is left. */
    static boolean deleteAll(File file)
    {
        BasicFileAttributes attributes = attributes(file);
        if (attributes != null && attributes.isDirectory())
        {
            File[] files = file.listFiles();
            if (files != null)
            {
                for (File child : files)
                {
                    deleteAll(child);
                }
            }
        }
        return file.delete() || !exists(file);
    }

    /** A file, or a folder with all it holds, copied to a new name; each file through a temporary one, so that none is ever there in part. Links are left out. */
    static void copyAll(File source, File target) throws IOException
    {
        BasicFileAttributes attributes = attributes(source);
        if (attributes == null || attributes.isSymbolicLink())
        {
            return;
        }
        if (attributes.isDirectory())
        {
            if (!target.mkdir())
            {
                throw new IOException("could not make " + target.getName());
            }
            File[] files = source.listFiles();
            if (files == null)
            {
                throw new IOException("could not read " + source.getName());
            }
            for (File file : files)
            {
                if (!file.getName().startsWith(TEMP))
                {
                    copyAll(file, new File(target, file.getName()));
                }
            }
            return;
        }
        File temp = tempBeside(target);
        try
        {
            copyFile(source, temp);
            // As the PC's Explorer keeps it: the load window lists saves by it.
            temp.setLastModified(attributes.lastModifiedTime().toMillis());
            if (!putInPlace(temp, target))
            {
                throw new IOException("could not put " + target.getName() + " in place");
            }
        }
        finally
        {
            temp.delete();
        }
    }

    /** The bytes of a file into another, which is made, or emptied first. */
    static void copyFile(File source, File target) throws IOException
    {
        byte[] buffer = new byte[1 << 16];
        try (InputStream in = new FileInputStream(source); FileOutputStream out = new FileOutputStream(target))
        {
            for (int n; (n = in.read(buffer)) > 0; )
            {
                out.write(buffer, 0, n);
            }
        }
    }

    // ---- the Files app opened at a folder ----

    /**
     * The intent that opens the system's Files app at a folder of the two places, for a file at the folder it is in, as
     * the PC opens Explorer there (MainActivity.openFolderUtf8: mods' "open folder" buttons); null for any other path, a
     * path that is not there, or when no Files app is found.
     */
    static Intent folderIntent(Context context, File path)
    {
        File folder = path.isDirectory() ? path : path.isFile() ? path.getParentFile() : null;
        File ext = context.getExternalFilesDir(null);
        String documentId = folder != null && ext != null ? documentIdOf(ext, folder) : null;
        return documentId != null ? inFilesApp(context, documentId) : null;
    }

    /** The intent that opens the Files app at the data folder, with the Mods beside it (the shortcut on the launcher icon), or null. */
    static Intent placesIntent(Context context)
    {
        return inFilesApp(context, DATA);
    }

    /**
     * The system's own Files app, which is the document chooser's app (its package differs: Google's or the maker's
     * build of DocumentsUI), asked to show a folder. Only it may read the provider (MANAGE_DOCUMENTS); another app that
     * opens folders could not, so none is asked.
     */
    private static Intent inFilesApp(Context context, String documentId)
    {
        PackageManager packages = context.getPackageManager();
        Uri uri = DocumentsContract.buildDocumentUri(authority(context), documentId);
        Intent view = new Intent(Intent.ACTION_VIEW).setDataAndType(uri, Document.MIME_TYPE_DIR).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        ComponentName chooser = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*").resolveActivity(packages);
        String[] candidates = { chooser != null ? chooser.getPackageName() : null, "com.google.android.documentsui", "com.android.documentsui" };
        for (String candidate : candidates)
        {
            if (candidate == null || packages.checkPermission(android.Manifest.permission.MANAGE_DOCUMENTS, candidate) != PackageManager.PERMISSION_GRANTED)
            {
                continue;
            }
            Intent own = new Intent(view).setPackage(candidate);
            if (own.resolveActivity(packages) != null)
            {
                return own;
            }
        }
        return null;
    }
}

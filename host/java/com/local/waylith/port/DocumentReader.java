package com.local.waylith.port;

import android.content.ContentProviderClient;
import android.content.ContentResolver;
import android.database.Cursor;
import android.net.Uri;
import android.os.DeadObjectException;
import android.os.ParcelFileDescriptor;
import android.os.RemoteException;

import java.io.IOException;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.List;

/**
 * Reads what the player picked with the system's file chooser through an unstable connection to the documents'
 * provider. ContentResolver.query and openInputStream hold a stable one while a cursor or a file is open, and when the
 * provider's process dies the system ends every process with a stable connection to it: a tester's Samsung (5 GB) lost
 * com.android.externalstorage while the game was being copied, and the game's process with it ("depends on provider
 * ... in dying proc", 2026-10-21). Here the provider's death is an exception instead: the connection is made again (the
 * system starts the provider anew) and the call repeated, a few times. A file already open stays readable: the
 * external storage provider hands out the file itself.
 */
final class DocumentReader
{
    private static final int RETRIES = 5;

    private final ContentResolver resolver;
    private final String authority;
    private ContentProviderClient client;

    DocumentReader(ContentResolver resolver, Uri uri)
    {
        this.resolver = resolver;
        this.authority = uri.getAuthority();
    }

    private synchronized ContentProviderClient client() throws IOException
    {
        if (client == null)
        {
            client = resolver.acquireUnstableContentProviderClient(authority);
            if (client == null)
            {
                throw new IOException("no provider for " + authority);
            }
        }
        return client;
    }

    /** The provider died under this connection: the next call makes a new one, after a moment for the provider to come back. */
    private void lost(ContentProviderClient dead, int attempt, Exception why) throws IOException
    {
        synchronized (this)
        {
            if (client == dead)
            {
                client.close();
                client = null;
            }
        }
        if (attempt >= RETRIES)
        {
            throw new IOException("系统的文件服务（" + authority + "）反复停止", why);
        }
        try
        {
            Thread.sleep(1000L * (attempt + 1));
        }
        catch (InterruptedException interrupted)
        {
            throw new IOException("interrupted");
        }
    }

    /**
     * The rows of a query, every column as a string, read whole so that no cursor stays open. A cursor whose provider
     * died stops early without saying so (its next window cannot be fetched): fewer rows than it counted is taken as that.
     */
    List<String[]> query(Uri uri, String[] columns) throws IOException
    {
        for (int attempt = 0; ; attempt++)
        {
            ContentProviderClient current = client();
            try
            {
                Cursor cursor = current.query(uri, columns, null, null, null);
                if (cursor == null)
                {
                    throw new IOException("cannot list " + uri);
                }
                try
                {
                    int count = cursor.getCount();
                    List<String[]> rows = new ArrayList<String[]>(Math.max(count, 0));
                    while (cursor.moveToNext())
                    {
                        String[] row = new String[columns.length];
                        for (int i = 0; i < columns.length; i++)
                        {
                            row[i] = cursor.getString(i);
                        }
                        rows.add(row);
                    }
                    if (rows.size() < count)
                    {
                        throw new DeadObjectException("the listing stopped after " + rows.size() + " of " + count);
                    }
                    return rows;
                }
                finally
                {
                    cursor.close();
                }
            }
            catch (DeadObjectException dead)
            {
                lost(current, attempt, dead);
            }
            catch (RemoteException problem)
            {
                throw new IOException(problem);
            }
        }
    }

    /** A document opened for reading. */
    InputStream open(Uri uri) throws IOException
    {
        for (int attempt = 0; ; attempt++)
        {
            ContentProviderClient current = client();
            try
            {
                ParcelFileDescriptor file = current.openFile(uri, "r");
                if (file == null)
                {
                    throw new IOException("cannot open " + uri);
                }
                return new ParcelFileDescriptor.AutoCloseInputStream(file);
            }
            catch (DeadObjectException dead)
            {
                lost(current, attempt, dead);
            }
            catch (RemoteException problem)
            {
                throw new IOException(problem);
            }
        }
    }

    synchronized void close()
    {
        if (client != null)
        {
            client.close();
            client = null;
        }
    }
}

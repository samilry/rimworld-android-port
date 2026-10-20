package com.local.waylith.port;

import android.app.Service;
import android.content.Intent;
import android.os.IBinder;
import android.util.Log;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;

/**
 * Runs setup/PortSetup.dll (setup/Setup.cs) on the player's imported game, in a process of its own (":setup" in the
 * manifest): the .NET runtime can be started only once in a process, and the game's own process still has to start it
 * for the game afterwards. MainActivity.prepareGame starts this and waits for setup/result.txt; this writes a result of
 * its own when the runtime did not even start. The process ends when the work is done.
 */
public final class SetupService extends Service
{
    private static native int runSetup(String ext, String root, String libdir, String tmp, boolean safe);

    @Override
    public int onStartCommand(Intent intent, int flags, int startId)
    {
        // A foreground service, so that the system keeps this (large) process while the player looks at another app.
        ImportService.goForeground(this, ImportService.SETUP_ID, "正在处理游戏文件，请稍候");
        final String root = intent != null && intent.getStringExtra("root") != null ? intent.getStringExtra("root") : "";
        final File ext = getExternalFilesDir(null);
        final String libdir = getApplicationInfo().nativeLibraryDir;
        final boolean safe = intent != null && intent.getBooleanExtra("safe", false);
        final String tmp = new File(getFilesDir(), "tmp").getPath();
        new Thread(new Runnable()
        {
            @Override
            public void run()
            {
                int code;
                try
                {
                    // The runtime's crypto shim (Cecil hashes public keys with SHA1 when it writes an assembly) has to be
                    // loaded through Java first, so that its JNI_OnLoad gets the VM and this app's class loader; the
                    // game's process does the same (host.c, java_load_library). Without it: a null pointer in the shim.
                    System.load(libdir + "/libSystem.Security.Cryptography.Native.Android.so");
                    System.loadLibrary("porthost");
                    code = runSetup(ext.getPath(), root, libdir, tmp, safe);
                }
                catch (Throwable problem)
                {
                    Log.e("port17", "setup could not run", problem);
                    code = -10;
                }
                if (code < 0)
                {
                    try
                    {
                        FileOutputStream out = new FileOutputStream(new File(ext, "setup/result.txt"));
                        String hresult = MainActivity.readText(new File(ext, "setup/init-error.txt")).trim();
                        out.write(("error\n处理程序没能启动（" + code + (hresult.isEmpty() ? "" : "，" + hresult) + "）。").getBytes("UTF-8"));
                        out.close();
                    }
                    catch (IOException ignored)
                    {
                    }
                }
                Diagnostics.trail(SetupService.this, "setup process finished: " + code + (safe ? " (second try)" : ""));
                stopSelf();
                // The runtime does not go away by itself; the process has done its job.
                android.os.Process.killProcess(android.os.Process.myPid());
            }
        }, "PortSetup").start();
        return START_NOT_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent)
    {
        // MainActivity binds as important while the work runs (the process is then scheduled as the app on the screen).
        return new android.os.Binder();
    }

    @Override
    public void onCreate()
    {
        super.onCreate();
        Diagnostics.recordJavaCrashes(this);
    }
}

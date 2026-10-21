package com.local.waylith.port;

import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.os.IBinder;
import android.util.Log;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.InputStreamReader;

/**
 * Keeps the game's process a foreground one while the game runs, doing nothing else. Away from the screen the game
 * draws nothing and, without this, its process is an ordinary background one, which a phone short of memory ends at
 * once: a tester's vivo (11 GB, the game 1.4 GB with 1.5 GB more swapped out) lost the game this way eight times in one
 * afternoon, the last time while hosting a multiplayer session, which ended for everyone (2026-10-21). A look at a chat
 * app, or sending the host's address to a friend, was enough. As a foreground service the process ranks with what the
 * player sees and is ended last. The notification is shown only where the player allows the app's notifications; the
 * service works either way. mobile.txt keepAlive=0 leaves it out.
 *
 * Swiping the game away in the recent apps ends the process here (onTaskRemoved): the service would otherwise keep the
 * game running without a window.
 */
public final class GameService extends Service
{
    private static final int ID = 19;

    static void start(Context context)
    {
        if (!wanted(context))
        {
            return;
        }
        try
        {
            context.startService(new Intent(context, GameService.class));
        }
        catch (Throwable problem)
        {
            Log.w("port17", "game service not started", problem);
        }
    }

    static void stop(Context context)
    {
        try
        {
            context.stopService(new Intent(context, GameService.class));
        }
        catch (Throwable ignored)
        {
        }
    }

    /** False when mobile.txt says keepAlive=0. */
    private static boolean wanted(Context context)
    {
        File settings = new File(context.getExternalFilesDir(null), "game/mobile.txt");
        try (BufferedReader in = new BufferedReader(new InputStreamReader(new FileInputStream(settings), "UTF-8")))
        {
            String line;
            while ((line = in.readLine()) != null)
            {
                String[] parts = line.split("=", 2);
                if (parts.length == 2 && parts[0].trim().equals("keepAlive"))
                {
                    String value = parts[1].trim();
                    return !value.equals("0") && !value.equalsIgnoreCase("false");
                }
            }
        }
        catch (Throwable ignored)
        {
        }
        return true;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId)
    {
        ImportService.goForeground(this, ID, ImportService.notification(this, "game", "游戏运行", android.R.drawable.ic_media_play,
            "游戏在运行，切到别的应用时不会被系统清掉"));
        return START_NOT_STICKY;
    }

    @Override
    public void onTaskRemoved(Intent rootIntent)
    {
        Diagnostics.trail(this, "the game was swiped away in the recent apps");
        Diagnostics.noteSelfExit(this);
        android.os.Process.killProcess(android.os.Process.myPid());
    }

    @Override
    public IBinder onBind(Intent intent)
    {
        return null;
    }
}

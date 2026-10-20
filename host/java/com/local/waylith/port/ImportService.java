package com.local.waylith.port;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;

/**
 * Keeps the game's process alive while the player's game is imported (MainActivity.prepareGame): a foreground service
 * of its own process, doing nothing. Without it the system's file chooser, or the player looking at another app while
 * the game is copied, leaves the process in the background, and a phone short of memory ends it there within a minute
 * or two (a tester's Redmi Note 13 Pro, 2026-10-20: killed again and again, the import never finished). SetupService
 * does the same for its own process with notification() below.
 */
public final class ImportService extends Service
{
    private static final String CHANNEL = "import";
    static final int ID = 17, SETUP_ID = 18;

    static void start(Context context)
    {
        try
        {
            context.startService(new Intent(context, ImportService.class));
        }
        catch (Throwable problem)
        {
            Log.w("port17", "import service not started", problem);
        }
    }

    static void stop(Context context)
    {
        try
        {
            context.stopService(new Intent(context, ImportService.class));
        }
        catch (Throwable ignored)
        {
        }
    }

    /** The notification of the import; shown only where the player allows the app's notifications, the service runs either way. */
    static Notification notification(Context context, String text)
    {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        if (manager != null && manager.getNotificationChannel(CHANNEL) == null)
        {
            manager.createNotificationChannel(new NotificationChannel(CHANNEL, "导入游戏", NotificationManager.IMPORTANCE_LOW));
        }
        return new Notification.Builder(context, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("Port17")
            .setContentText(text)
            .setOngoing(true)
            .build();
    }

    /** Makes the service a foreground one (a data transfer); false where the system does not allow it now. */
    static boolean goForeground(Service service, int id, String text)
    {
        try
        {
            if (Build.VERSION.SDK_INT >= 29)
            {
                service.startForeground(id, notification(service, text), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
            }
            else
            {
                service.startForeground(id, notification(service, text));
            }
            return true;
        }
        catch (Throwable problem)
        {
            Log.w("port17", "not a foreground service", problem);
            return false;
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId)
    {
        goForeground(this, ID, "正在导入游戏文件，请稍候");
        return START_NOT_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent)
    {
        return null;
    }
}

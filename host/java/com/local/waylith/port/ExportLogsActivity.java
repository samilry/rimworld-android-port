package com.local.waylith.port;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.DialogInterface;
import android.os.Bundle;
import android.widget.Toast;

/**
 * "导出日志" on the launcher icon (Diagnostics.addShortcut): packs the logs into Download/Port17 without a crash
 * having happened (for a problem the game survives), and says where. A translucent activity of its own: the game's
 * activity is not started for it.
 */
public final class ExportLogsActivity extends Activity
{
    @Override
    protected void onCreate(Bundle savedInstanceState)
    {
        super.onCreate(savedInstanceState);
        new Thread(new Runnable()
        {
            @Override
            public void run()
            {
                final String where = Diagnostics.export(ExportLogsActivity.this, null);
                runOnUiThread(new Runnable()
                {
                    @Override
                    public void run()
                    {
                        String text = where != null
                            ? "日志已保存到：" + where + "\n\n请把这个文件发给作者（里面是游戏日志和手机型号，API 密钥已打码）。"
                            : "日志没能保存。";
                        // The player may have left meanwhile (packing big logs takes a few seconds): a dialog has no
                        // window to go on then (BadTokenException, a tester's vivo, 2026-10-21), a toast does.
                        if (isFinishing() || isDestroyed())
                        {
                            Toast.makeText(getApplicationContext(), text, Toast.LENGTH_LONG).show();
                            return;
                        }
                        try
                        {
                            show(text);
                        }
                        catch (RuntimeException gone)
                        {
                            Toast.makeText(getApplicationContext(), text, Toast.LENGTH_LONG).show();
                            finish();
                        }
                    }
                });
            }
        }, "ExportLogs").start();
    }

    private void show(String text)
    {
        new AlertDialog.Builder(this)
            .setTitle("导出日志")
            .setMessage(text)
            .setPositiveButton("好的", null)
            .setOnDismissListener(new DialogInterface.OnDismissListener()
            {
                @Override
                public void onDismiss(DialogInterface dialog)
                {
                    finish();
                }
            })
            .show();
    }
}

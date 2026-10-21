package com.local.waylith.port;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ComponentName;
import android.content.DialogInterface;
import android.content.Intent;
import android.os.Bundle;

import java.io.File;

/**
 * "重新导入游戏" on the launcher icon of the tester's APK (Diagnostics.addShortcut): the game is taken again from
 * another copy (the PC's RimWorld was updated, or the first import was another version), keeping saves, settings and
 * mods. game/port-setup.txt is put aside as ASIDE: the host then finds the game not prepared, and
 * MainActivity.prepareGame asks for a copy as on the first start; leaving that dialog puts the old file back (the game
 * as it was), a successful setup drops it. The game's process ends here (this activity runs in it) and starts afresh.
 */
public final class ReimportActivity extends Activity
{
    static final String ASIDE = "game/port-setup.reimport.txt";

    @Override
    protected void onCreate(Bundle savedInstanceState)
    {
        super.onCreate(savedInstanceState);
        final File ext = getExternalFilesDir(null);
        String version = MainActivity.readText(new File(ext, "game/Version.txt")).trim();
        int end = version.indexOf('\n');
        version = end < 0 ? version : version.substring(0, end).trim();
        // The version this APK was made for: from the installed APK (assets/version.txt, host/build.sh), as the unpacked
        // setup/version.txt is the previous APK's until the game has been started once after an update.
        String[] rules = installedRules().split("\n");
        if (rules.length < 2)
        {
            rules = MainActivity.readText(new File(ext, "setup/version.txt")).split("\n");
        }
        String supported = rules.length > 1 ? rules[1].trim() : "";
        final boolean imported = new File(ext, "game/port-setup.txt").exists();
        String text = "换成另一份 RimWorld，例如电脑上的游戏更新到了新版本。\n\n"
            + (version.isEmpty() ? "" : "现在的游戏版本：" + version + "\n")
            + (supported.isEmpty() ? "" : "这个移植版是按 " + supported + " 做的。\n")
            + "\n存档、设置和 mod 都会保留，只替换游戏本体。\n\n"
            + "点“重新导入”后 Port17 会关闭再打开（游戏正开着的话请先存档），然后请你选择新的游戏文件，和第一次导入一样；"
            + "那时点“退出”就是取消，下次打开照常用现在的游戏。";
        AlertDialog.Builder builder = new AlertDialog.Builder(this)
            .setTitle("重新导入游戏")
            .setOnDismissListener(new DialogInterface.OnDismissListener()
            {
                @Override
                public void onDismiss(DialogInterface dialog)
                {
                    finish();
                }
            });
        if (!imported)
        {
            builder.setMessage("还没有导入过游戏，直接打开 Port17 就会请你选择游戏文件。").setPositiveButton("好的", null).show();
            return;
        }
        builder.setMessage(text)
            .setNegativeButton("取消", null)
            .setPositiveButton("重新导入", new DialogInterface.OnClickListener()
            {
                @Override
                public void onClick(DialogInterface dialog, int which)
                {
                    File aside = new File(ext, ASIDE);
                    aside.delete();
                    boolean moved = new File(ext, "game/port-setup.txt").renameTo(aside);
                    Diagnostics.trail(ReimportActivity.this, "re-import asked for" + (moved ? "" : " (port-setup.txt could not be put aside)"));
                    Diagnostics.noteSelfExit(ReimportActivity.this);
                    startActivity(Intent.makeRestartActivityTask(new ComponentName(ReimportActivity.this, MainActivity.class)));
                    android.os.Process.killProcess(android.os.Process.myPid());
                }
            })
            .show();
    }

    /** assets/version.txt of the installed APK ("" when it has none: an APK from before 2026-10-21). */
    private String installedRules()
    {
        try
        {
            java.io.InputStream in = getAssets().open("version.txt");
            try
            {
                java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
                byte[] buffer = new byte[256];
                int n;
                while ((n = in.read(buffer)) > 0)
                {
                    out.write(buffer, 0, n);
                }
                return new String(out.toByteArray(), "UTF-8");
            }
            finally
            {
                in.close();
            }
        }
        catch (java.io.IOException e)
        {
            return "";
        }
    }
}

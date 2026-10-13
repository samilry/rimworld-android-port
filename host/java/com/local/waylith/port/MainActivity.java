package com.local.waylith.port;

import android.app.NativeActivity;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;

/**
 * The game's activity: a NativeActivity (the host library is named by the manifest's android.app.lib_name) that
 * takes the whole screen. Without this the window is laid out away from the display cutout (141 px on this phone)
 * and the status and navigation bars stay over the game. The screen is kept on while the game runs.
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

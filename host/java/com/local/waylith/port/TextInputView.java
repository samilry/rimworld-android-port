package com.local.waylith.port;

import android.content.Context;
import android.text.InputType;
import android.view.KeyEvent;
import android.view.View;
import android.view.inputmethod.BaseInputConnection;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;

/**
 * What the soft keyboard talks to. A NativeActivity's own view offers the input method no InputConnection, so an
 * IME that composes (Chinese pinyin, for one) has nowhere to commit its text; this invisible view takes the focus
 * while a text field in the game has it, and hands committed text and editing keys to the native side (the GLFW
 * shim), which turns them into the character and key events the engine expects. Composition in progress is not
 * shown in the game; only what the IME commits arrives.
 */
public final class TextInputView extends View
{
    static
    {
        System.loadLibrary("glfw");
    }

    private boolean active;

    public TextInputView(Context context)
    {
        super(context);
        // Focusable only while the game has a text field focused: a focused editor makes Android show the keyboard
        // by itself, and keep it.
        setFocusable(false);
        setFocusableInTouchMode(false);
    }

    /** Takes or gives up the focus (called on the UI thread by the activity). */
    public void setActive(boolean on)
    {
        active = on;
        setFocusable(on);
        setFocusableInTouchMode(on);
        if (on)
        {
            requestFocus();
        }
        else
        {
            clearFocus();
        }
    }

    private static native void nativeCommit(String text);

    private static native void nativeKey(int keyCode, int action);

    @Override
    public boolean onCheckIsTextEditor()
    {
        return active;
    }

    @Override
    public InputConnection onCreateInputConnection(EditorInfo outAttrs)
    {
        if (!active)
        {
            return null;
        }
        outAttrs.inputType = InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS;
        outAttrs.imeOptions = EditorInfo.IME_ACTION_DONE | EditorInfo.IME_FLAG_NO_FULLSCREEN | EditorInfo.IME_FLAG_NO_EXTRACT_UI;
        return new Connection(this);
    }

    private static final class Connection extends BaseInputConnection
    {
        Connection(View target)
        {
            super(target, true);
        }

        @Override
        public boolean commitText(CharSequence text, int newCursorPosition)
        {
            if (text != null && text.length() > 0)
            {
                nativeCommit(text.toString());
            }
            // The editable is kept empty: the game holds the text, and the IME must not try to edit what it sent.
            return super.commitText("", 1);
        }

        @Override
        public boolean setComposingText(CharSequence text, int newCursorPosition)
        {
            return super.setComposingText(text, newCursorPosition);
        }

        @Override
        public boolean finishComposingText()
        {
            // Some IMEs finish a composition without committing it (a tap elsewhere): what was composed is sent.
            CharSequence composed = getComposingText();
            if (composed != null && composed.length() > 0)
            {
                nativeCommit(composed.toString());
                super.commitText("", 1);
                return true;
            }
            return super.finishComposingText();
        }

        private CharSequence getComposingText()
        {
            android.text.Editable editable = getEditable();
            if (editable == null)
            {
                return null;
            }
            int start = getComposingSpanStart(editable), end = getComposingSpanEnd(editable);
            return start >= 0 && end > start ? editable.subSequence(start, end) : null;
        }

        @Override
        public boolean deleteSurroundingText(int beforeLength, int afterLength)
        {
            for (int i = 0; i < beforeLength; i++)
            {
                nativeKey(KeyEvent.KEYCODE_DEL, KeyEvent.ACTION_DOWN);
                nativeKey(KeyEvent.KEYCODE_DEL, KeyEvent.ACTION_UP);
            }
            for (int i = 0; i < afterLength; i++)
            {
                nativeKey(KeyEvent.KEYCODE_FORWARD_DEL, KeyEvent.ACTION_DOWN);
                nativeKey(KeyEvent.KEYCODE_FORWARD_DEL, KeyEvent.ACTION_UP);
            }
            return true;
        }

        @Override
        public boolean sendKeyEvent(KeyEvent event)
        {
            nativeKey(event.getKeyCode(), event.getAction());
            return true;
        }

        @Override
        public boolean performEditorAction(int actionCode)
        {
            nativeKey(KeyEvent.KEYCODE_ENTER, KeyEvent.ACTION_DOWN);
            nativeKey(KeyEvent.KEYCODE_ENTER, KeyEvent.ACTION_UP);
            return true;
        }
    }
}

package com.local.waylith.port;

/**
 * The little Java side of the host. The NativeActivity's native code calls these through JNI (looked up through the
 * activity's class loader, since a native thread's FindClass only knows the system classes).
 */
public final class Boot
{
    private Boot()
    {
    }

    /**
     * Loads a native library from Java, so that its JNI_OnLoad runs with the application's class loader: the .NET
     * runtime's Android crypto shim needs that to find its helper classes and to get hold of the JavaVM.
     */
    public static String load(String path)
    {
        try
        {
            System.load(path);
            return null;
        }
        catch (Throwable problem)
        {
            return problem.toString();
        }
    }
}

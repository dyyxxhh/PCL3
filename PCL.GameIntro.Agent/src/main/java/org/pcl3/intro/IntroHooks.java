package org.pcl3.intro;

import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.lang.management.ManagementFactory;
import java.lang.reflect.Method;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

public final class IntroHooks {
    private static final ArrayBlockingQueue<String> EVENTS = new ArrayBlockingQueue<>(32);
    private static final CountDownLatch WINDOW_DECISION = new CountDownLatch(1);
    private static final CountDownLatch FIRST_FRAME = new CountDownLatch(1);
    private static volatile boolean firstFrameHook;
    private static volatile boolean waitForFirstFrame;
    private static final Object AUDIO = new Object();
    private static volatile Socket connection;
    private static volatile boolean windowReady;
    private static volatile boolean holding;
    private static volatile boolean restoreGain;
    private static float requestedGain = 1;
    private static boolean gainSet;
    private static boolean holdRequested;
    private static boolean spaceConsumed;
    private static boolean readinessSent;
    private static ClassLoader gameLoader;
    private static long boundWindow;
    private static boolean bypassed;

    private IntroHooks() { }

    public static boolean connect() {
        String endpoint = System.getenv("PCL_GAME_INTRO_ENDPOINT");
        if (endpoint == null || !endpoint.matches("[1-9][0-9]{0,4}:[0-9a-f]{64}")) return false;
        int separator = endpoint.indexOf(':');
        int port = Integer.parseInt(endpoint.substring(0, separator));
        if (port > 65535) return false;
        String pid = ManagementFactory.getRuntimeMXBean().getName().split("@", 2)[0];
        if (!pid.matches("[1-9][0-9]{0,9}")) return false;
        Socket socket = new Socket();
        try {
            socket.connect(new InetSocketAddress(InetAddress.getByAddress(new byte[] {127, 0, 0, 1}), port), 250);
            socket.setTcpNoDelay(true);
        } catch (IOException failure) {
            close(socket);
            return false;
        }
        connection = socket;
        offer("HELLO " + endpoint.substring(separator + 1) + " " + pid);
        daemon(() -> writeEvents(socket), "events");
        daemon(() -> readCommands(socket), "commands");
        return true;
    }

    private static void daemon(Runnable body, String name) {
        Thread worker = new Thread(body, "PCL3 intro " + name);
        worker.setDaemon(true);
        worker.start();
    }

    private static void writeEvents(Socket socket) {
        try {
            BufferedWriter output = new BufferedWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.US_ASCII));
            while (connection == socket) {
                String event = EVENTS.poll(500, TimeUnit.MILLISECONDS);
                if (event == null) continue;
                output.write(event);
                output.write('\n');
                output.flush();
            }
        } catch (IOException ignored) {
            // Connection loss uses the same release path as an orderly handoff.
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
        } finally {
            // A failed event writer requests shutdown without releasing game input or gain.
            if (connection == socket) {
                try { socket.shutdownOutput(); } catch (IOException ignored) { }
            }
        }
    }

    private static void readCommands(Socket socket) {
        try {
            BufferedReader input = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.US_ASCII));
            String command;
            while ((command = readCommand(input)) != null) {
                if (command.equals("RELEASE")) return;
                if (command.equals("COVERED") && waitForFirstFrame) {
                    FIRST_FRAME.countDown();
                    continue;
                }
                boolean gateFrame = command.equals("HOLD_FRAME");
                if (!gateFrame && !command.equals("HOLD")) return;
                synchronized (AUDIO) {
                    if (connection != socket || gameLoader == null || holdRequested || gainSet || bypassed) return;
                    if (gateFrame && !firstFrameHook) {
                        incompatible();
                        continue;
                    }
                    // The window is created before initializing OpenAL. All later gain writes are gated.
                    holdRequested = true;
                    waitForFirstFrame = gateFrame;
                    holding = true;
                    offer("HELD");
                    WINDOW_DECISION.countDown();
                }
            }
        } catch (IOException ignored) {
            // If the transport is already gone, fail open rather than leaving the game muted.
        } finally {
            disable();
        }
    }

    private static String readCommand(BufferedReader input) throws IOException {
        StringBuilder command = new StringBuilder(16);
        for (int i = 0; i <= 16; i++) {
            int ch = input.read();
            if (ch == -1) return null;
            if (ch == '\n') {
                int length = command.length();
                if (length > 0 && command.charAt(length - 1) == '\r') command.setLength(length - 1);
                return command.toString();
            }
            if (i == 16) return null;
            command.append((char) ch);
        }
        return null;
    }

    private static void close(Socket socket) {
        try { socket.close(); } catch (IOException ignored) { }
    }

    public static void incompatible() {
        synchronized (AUDIO) {
            if (bypassed) return;
            bypassed = true;
            offer("BYPASS");
            WINDOW_DECISION.countDown();
            FIRST_FRAME.countDown();
        }
    }

    private static void disable() {
        synchronized (AUDIO) {
            Socket socket = connection;
            if (socket == null) return;
            connection = null;
            restoreGain = holding && gainSet;
            holding = false;
            WINDOW_DECISION.countDown();
            FIRST_FRAME.countDown();
            close(socket);
        }
    }

    public static void windowCreated(long glfw, Class<?> gameWindowClass) {
        bindWindow(glfw, gameWindowClass, false);
    }

    public static void earlyWindowCreated(long glfw, Class<?> displayClass) {
        bindWindow(glfw, displayClass, true);
    }

    public static void confirmWindow(long glfw, Class<?> gameWindowClass) {
        synchronized (AUDIO) {
            if (connection == null || bypassed) return;
            if (boundWindow == 0 || boundWindow != glfw) {
                incompatible();
                return;
            }
            if (gameWindowClass.getClassLoader() == null) {
                incompatible();
                return;
            }
            gameLoader = gameWindowClass.getClassLoader();
            windowReady = true;
        }
    }

    public static void sdlWindowCreated(long sdl, Class<?> gameWindowClass) {
        bindWindow(sdl, gameWindowClass, false, true);
    }

    private static void bindWindow(long handle, Class<?> gameWindowClass, boolean early) {
        bindWindow(handle, gameWindowClass, early, false);
    }

    private static void bindWindow(long handle, Class<?> gameWindowClass, boolean early, boolean sdl) {
        if (connection == null || handle == 0) return;
        ClassLoader loader = gameWindowClass.getClassLoader();
        String os = System.getProperty("os.name", "").toLowerCase(Locale.ROOT);
        try {
            String backend = os.startsWith("windows") ? "Win32" : "X11";
            long window;
            if (sdl) {
                Class<?> video = Class.forName("org.lwjgl.sdl.SDLVideo", true, loader);
                String driver = (String) video.getMethod("SDL_GetCurrentVideoDriver").invoke(null);
                boolean windows = backend.equals("Win32");
                if (!(windows ? "windows" : "x11").equals(driver)) { incompatible(); return; }
                int properties = (Integer) video.getMethod("SDL_GetWindowProperties", long.class).invoke(null, handle);
                Class<?> sdlProperties = Class.forName("org.lwjgl.sdl.SDLProperties", true, loader);
                Method read = sdlProperties.getMethod(windows ? "SDL_GetPointerProperty" : "SDL_GetNumberProperty",
                    int.class, CharSequence.class, long.class);
                window = (Long) read.invoke(null, properties, windows ? "SDL.window.win32.hwnd" : "SDL.window.x11.window", 0L);
            } else {
                Class<?> nativeAccess = Class.forName("org.lwjgl.glfw.GLFWNative" + backend, true, loader);
                window = (Long) nativeAccess.getMethod("glfwGet" + backend + "Window", long.class).invoke(null, handle);
            }
            if (window == 0) { incompatible(); return; }
            synchronized (AUDIO) {
                if (connection == null || bypassed || boundWindow != 0) return;
                boundWindow = handle;
                gameLoader = loader;
                windowReady = !early;
                offer("WINDOW " + backend + " " + Long.toUnsignedString(window));
            }
            // This handshake precedes the first frame. A missing helper can never stall game startup indefinitely.
            if (!WINDOW_DECISION.await(350, TimeUnit.MILLISECONDS)) disable();
        } catch (ReflectiveOperationException | RuntimeException | LinkageError failure) {
            incompatible();
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
            disable();
        }
    }

    static void firstFrameHookInstalled() { firstFrameHook = true; }

    public static void beforeFirstSwap(long glfw) {
        if (!waitForFirstFrame || FIRST_FRAME.getCount() == 0 || glfw != boundWindow) return;
        try {
            if (!FIRST_FRAME.await(350, TimeUnit.MILLISECONDS)) incompatible();
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
            incompatible();
        }
    }

    public static boolean suppressKey(int key, int action) {
        if (key != 32) return holding;
        if (action == 0) {
            boolean consumed = spaceConsumed;
            spaceConsumed = false;
            offer("SPACE_UP");
            return consumed || holding;
        }
        if (action == 1) offer("SPACE_DOWN");
        if (holding) spaceConsumed = true;
        return spaceConsumed;
    }

    public static boolean suppressPointer() { return holding; }

    public static float filterGain(float gain) {
        synchronized (AUDIO) {
            gainSet = true;
            requestedGain = gain;
            if (!holding) restoreGain = false;
            return holding ? 0 : gain;
        }
    }

    // 26.3 adjusts individual source gains. Mute the newly initialized listener instead,
    // leaving those source gains intact for handoff and letting OpenAL supply its default (1).
    public static void muteNewListener() {
        synchronized (AUDIO) {
            if (!holding || gameLoader == null) return;
            try {
                setListenerGain(0);
                gainSet = true;
                requestedGain = 1;
            } catch (ReflectiveOperationException | RuntimeException | LinkageError failure) {
                incompatible();
            }
        }
    }

    public static void framePresented(boolean initialized, Object overlay, Object screen) {
        if (restoreGain) applyGainOnGameThread();
        if (connection == null || readinessSent || !initialized || overlay != null || screen == null || !windowReady) return;
        readinessSent = true;
        // Do not hide accessibility onboarding, required warnings or a recovery screen.
        String screenName = screen.getClass().getName();
        offer(screenName.equals("fof") || screenName.equals("gsd") || screenName.equals("net.minecraft.class_442") ||
            screenName.equals("net.minecraft.client.gui.screens.TitleScreen") ? "READY" : "BYPASS");
    }

    private static void applyGainOnGameThread() {
        synchronized (AUDIO) {
            if (!restoreGain || gameLoader == null) return;
            restoreGain = false;
            try {
                setListenerGain(requestedGain);
            } catch (ReflectiveOperationException | RuntimeException | LinkageError ignored) {
                // A restarted sound engine will receive the stored game volume through its own gain write.
            }
        }
    }

    private static void setListenerGain(float value) throws ReflectiveOperationException {
        Class<?> al = Class.forName("org.lwjgl.openal.AL10", true, gameLoader);
        Method gain = al.getMethod("alListenerf", int.class, float.class);
        gain.invoke(null, 0x100A, value);
    }

    private static void offer(String event) {
        Socket socket = connection;
        if (socket != null && !EVENTS.offer(event)) {
            try { socket.shutdownOutput(); } catch (IOException ignored) { }
        }
    }
}

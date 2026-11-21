package org.pcl3.intro;

import java.io.File;
import java.lang.instrument.Instrumentation;
import java.util.jar.JarFile;

public final class IntroAgent {
    private IntroAgent() { }

    public static void premain(String profile, Instrumentation instrumentation) {
        if (!"vanilla-1.21.1".equals(profile) && !"fabric-1.21.1-0.16.14".equals(profile) &&
            !"forge-1.21.1-52.1.20".equals(profile) && !"neoforge-1.21.1-21.1.172".equals(profile) &&
            !"vanilla-26.3".equals(profile) && !"vanilla-1.21.11".equals(profile)) return;
        try {
            File agent = new File(IntroAgent.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            if ("fabric-1.21.1-0.16.14".equals(profile)) {
                // Knot delegates this specific jar to its parent, leaving its normal isolation enabled.
                String key = "fabric.systemLibraries";
                String current = System.getProperty(key, "");
                String path = agent.getCanonicalPath();
                if (path.indexOf(File.pathSeparatorChar) >= 0) return;
                System.setProperty(key, current.isEmpty() ? path : current + File.pathSeparator + path);
            }
            try (JarFile jar = new JarFile(agent)) {
                instrumentation.appendToBootstrapClassLoaderSearch(jar);
            }
            // Load the engine and all its implementation classes from the same loader.
            Class.forName("org.pcl3.intro.AgentEngine", true, null)
                .getMethod("start", String.class, Instrumentation.class).invoke(null, profile, instrumentation);
        } catch (Exception | LinkageError unavailable) {
            // With no authenticated WINDOW/HELD handshake the player times out without covering the game.
        }
    }
}

package org.pcl3.intro;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;

public final class AgentEngine {
    private AgentEngine() { }

    public static void start(String profile, Instrumentation instrumentation) {
        ClassFileTransformer adapter;
        if ("vanilla-1.21.1".equals(profile)) adapter = new Vanilla1211();
        else if ("fabric-1.21.1-0.16.14".equals(profile)) adapter = new Fabric1211();
        else if ("forge-1.21.1-52.1.20".equals(profile)) adapter = new Forge52120();
        else if ("neoforge-1.21.1-21.1.172".equals(profile)) adapter = new NeoForge21172();
        else if ("vanilla-1.21.11".equals(profile)) adapter = new Vanilla12111();
        else if ("vanilla-26.3".equals(profile)) adapter = new Vanilla263();
        else return;
        try {
            if (IntroHooks.connect()) {
                if (!(adapter instanceof Vanilla263)) instrumentation.addTransformer(new FirstFramePatch());
                instrumentation.addTransformer(adapter);
            }
        } catch (RuntimeException | LinkageError unavailable) {
            IntroHooks.incompatible();
        }
    }
}

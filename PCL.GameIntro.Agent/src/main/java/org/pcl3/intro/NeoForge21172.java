package org.pcl3.intro;

import java.lang.instrument.ClassFileTransformer;
import java.security.MessageDigest;
import java.security.ProtectionDomain;
import java.util.HashMap;
import java.util.Map;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Label;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;

/** Exact initial-load profile for NeoForge 21.1.172 on Minecraft 1.21.1. */
final class NeoForge21172 implements ClassFileTransformer {
    private static final String HOOKS = "org/pcl3/intro/IntroHooks";
    private static final String DISPLAY = "net/neoforged/fml/earlydisplay/DisplayWindow";
    private static final String WINDOW = "com/mojang/blaze3d/platform/Window";
    private static final String GAME = "net/minecraft/client/Minecraft";
    private static final String KEYBOARD = "net/minecraft/client/KeyboardHandler";
    private static final String MOUSE = "net/minecraft/client/MouseHandler";
    private static final String LISTENER = "com/mojang/blaze3d/audio/Listener";
    private static final Map<String, String> TARGETS = new HashMap<>();

    static {
        TARGETS.put(DISPLAY, "bd1a0a80ac030ddf88f473571857ef8a56e48853043e1b5ec4d1bfd580fb4860");
        TARGETS.put(WINDOW, "01f54d36c7360e964751b72f6a8dfb149e5aa04a12e9c92a38d224431d9d1386");
        TARGETS.put(GAME, "b9ceabe091565234826b42f53135c9a1811bc9275ff9afce90bb7cca76b40ebd");
        TARGETS.put(KEYBOARD, "962bd8fa3756578fa32752238e1e91a9bc9ccaed8019b8829fc3b96e8843646b");
        TARGETS.put(MOUSE, "10ac3912e4b9f4af820cd2aa6a5fc79776b764dcb052f7d6694f86a1a1ac229c");
        TARGETS.put(LISTENER, "79949673ae024e7f0c3991cb76336c88694ec3cbbe0ff256ad21c8ce579093ca");
    }

    @Override
    public byte[] transform(ClassLoader loader, String name, Class<?> redefined, ProtectionDomain domain, byte[] bytes) {
        if (loader == null || redefined != null || !TARGETS.containsKey(name)) return null;
        try {
            byte[] transformed = patch(name, bytes);
            if (transformed != null && Class.forName(HOOKS.replace('/', '.'), false, loader) == IntroHooks.class)
                return transformed;
        } catch (Exception | LinkageError unavailable) {
            // Keep the original class if the profile or bridge cannot be used by this loader.
        }
        IntroHooks.incompatible();
        return null;
    }

    static byte[] patch(String name, byte[] bytes) throws Exception {
        String hash = TARGETS.get(name);
        if (hash == null || !fingerprint(bytes).equals(hash)) return null;
        ClassReader reader = new ClassReader(bytes);
        ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
        int[] changes = {0};
        reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
            @Override
            public MethodVisitor visitMethod(int access, String method, String descriptor, String signature, String[] exceptions) {
                MethodVisitor target = super.visitMethod(access, method, descriptor, signature, exceptions);
                boolean keyboard = KEYBOARD.equals(name) && method.equals("keyPress") && descriptor.equals("(JIIII)V");
                boolean pointer = MOUSE.equals(name) && (method.equals("onPress") && descriptor.equals("(JIII)V") ||
                    (method.equals("onScroll") || method.equals("onMove")) && descriptor.equals("(JDD)V"));
                boolean recovery = GAME.equals(name) && method.equals("abortResourcePackRecovery") && descriptor.equals("()V");
                boolean display = DISPLAY.equals(name) && method.equals("initWindow") && descriptor.equals("(Ljava/lang/String;)V");
                boolean handoff = WINDOW.equals(name) && method.equals("<init>");
                boolean frame = GAME.equals(name) && method.equals("runTick") && descriptor.equals("(Z)V");
                boolean gain = LISTENER.equals(name) && method.equals("setGain") && descriptor.equals("(F)V");
                if (!(keyboard || pointer || recovery || display || handoff || frame || gain)) return target;

                return new MethodVisitor(Opcodes.ASM9, target) {
                    @Override
                    public void visitCode() {
                        super.visitCode();
                        if (keyboard || pointer) {
                            Label processInput = new Label();
                            if (keyboard) {
                                super.visitVarInsn(Opcodes.ILOAD, 3); // GLFW key
                                super.visitVarInsn(Opcodes.ILOAD, 5); // GLFW action
                                callHook("suppressKey", "(II)Z");
                            } else {
                                callHook("suppressPointer", "()Z");
                            }
                            super.visitJumpInsn(Opcodes.IFEQ, processInput);
                            super.visitInsn(Opcodes.RETURN);
                            super.visitLabel(processInput);
                            super.visitFrame(Opcodes.F_SAME, 0, null, 0, null);
                        } else if (recovery) {
                            callHook("incompatible", "()V");
                        }
                    }

                    @Override
                    public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                        if (display && opcode == Opcodes.INVOKESTATIC && owner.equals("org/lwjgl/glfw/GLFW") &&
                            invoked.equals("glfwShowWindow") && type.equals("(J)V")) {
                            // FML passes this native window to Minecraft after showing its early display.
                            super.visitInsn(Opcodes.DUP2);
                            super.visitLdcInsn(Type.getObjectType(DISPLAY));
                            callHook("earlyWindowCreated", "(JLjava/lang/Class;)V");
                        }
                        if (gain && opcode == Opcodes.INVOKESTATIC && owner.equals("org/lwjgl/openal/AL10") &&
                            invoked.equals("alListenerf") && type.equals("(IF)V")) {
                            callHook("filterGain", "(F)F");
                        }
                        super.visitMethodInsn(opcode, owner, invoked, type, iface);
                        if (handoff && opcode == Opcodes.INVOKESTATIC && owner.equals("net/neoforged/fml/loading/ImmediateWindowHandler") &&
                            invoked.equals("setupMinecraftWindow") && type.equals("(Ljava/util/function/IntSupplier;" +
                                "Ljava/util/function/IntSupplier;Ljava/util/function/Supplier;Ljava/util/function/LongSupplier;)J")) {
                            super.visitInsn(Opcodes.DUP2);
                            super.visitLdcInsn(Type.getObjectType(WINDOW));
                            callHook("confirmWindow", "(JLjava/lang/Class;)V");
                        }
                        if (frame && opcode == Opcodes.INVOKEVIRTUAL && owner.equals(WINDOW) &&
                            invoked.equals("updateDisplay") && type.equals("()V")) {
                            loadGameField("gameLoadFinished", "Z");
                            loadGameField("overlay", "Lnet/minecraft/client/gui/screens/Overlay;");
                            loadGameField("screen", "Lnet/minecraft/client/gui/screens/Screen;");
                            callHook("framePresented", "(ZLjava/lang/Object;Ljava/lang/Object;)V");
                        }
                    }

                    private void loadGameField(String field, String type) {
                        super.visitVarInsn(Opcodes.ALOAD, 0);
                        super.visitFieldInsn(Opcodes.GETFIELD, GAME, field, type);
                    }

                    private void callHook(String hook, String type) {
                        super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, hook, type, false);
                        changes[0]++;
                    }
                };
            }
        }, 0);
        int expected = MOUSE.equals(name) ? 3 : GAME.equals(name) ? 2 : 1;
        return changes[0] == expected ? writer.toByteArray() : null;
    }

    private static String fingerprint(byte[] input) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(input);
        StringBuilder output = new StringBuilder(64);
        for (byte value : digest) {
            output.append(Character.forDigit((value >>> 4) & 15, 16));
            output.append(Character.forDigit(value & 15, 16));
        }
        return output.toString();
    }
}

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

/** Exact post-ModLauncher profile for Minecraft 1.21.1 with Forge 52.1.20. */
final class Forge52120 implements ClassFileTransformer {
    private static final String HOOKS = "org/pcl3/intro/IntroHooks";
    private static final String DISPLAY = "net/minecraftforge/fml/earlydisplay/DisplayWindow";
    private static final String WINDOW = "com/mojang/blaze3d/platform/Window";
    private static final String KEYBOARD = "net/minecraft/client/KeyboardHandler";
    private static final String MOUSE = "net/minecraft/client/MouseHandler";
    private static final String LISTENER = "com/mojang/blaze3d/audio/Listener";
    private static final String MINECRAFT = "net/minecraft/client/Minecraft";
    private static final Map<String, String> TARGETS = new HashMap<>();

    static {
        TARGETS.put(DISPLAY, "22dcdde938792491139cd1642b9b0d35e172df729fca4e960c4ab3514d032967");
        TARGETS.put(WINDOW, "af362162345e3a744f71b762f1942c488e7af40f95951bf81f0069f0cb66afdc");
        TARGETS.put(KEYBOARD, "4131f9a1fec9f3a405610d203292bba941a77c4b64d9685e194bda738f56871d");
        TARGETS.put(MOUSE, "7ded449f0bab62fbf474b5e96bad079e57a2889807c9c1384cc925cf8a0777ca");
        TARGETS.put(LISTENER, "8601e11174d3ed98985723aef0d4bd2211b8092281b5f997eccc0285e4840920");
        TARGETS.put(MINECRAFT, "33f1b30f94828397182b91ef795cd5d3dbcd3f21db58ec374ef9dc84a1027f2d");
    }

    @Override
    public byte[] transform(ClassLoader loader, String name, Class<?> redefined, ProtectionDomain domain, byte[] bytes) {
        if (redefined != null || !TARGETS.containsKey(name) || loader == null) return null;
        try {
            byte[] transformed = patch(name, bytes);
            if (transformed != null && Class.forName("org.pcl3.intro.IntroHooks", false, loader) == IntroHooks.class)
                return transformed;
        } catch (Exception | LinkageError failure) {
            // A loader that cannot resolve the shared bridge must keep its original classes.
        }
        IntroHooks.incompatible();
        return null;
    }

    static byte[] patch(String name, byte[] bytes) throws Exception {
        String expected = TARGETS.get(name);
        if (expected == null || !expected.equals(fingerprint(bytes))) return null;
        ClassReader reader = new ClassReader(bytes);
        ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
        int[] changes = {0};
        reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
            @Override
            public MethodVisitor visitMethod(int access, String method, String descriptor, String signature, String[] exceptions) {
                MethodVisitor target = super.visitMethod(access, method, descriptor, signature, exceptions);
                if (KEYBOARD.equals(name) && method.equals("keyPress") && descriptor.equals("(JIIII)V"))
                    return gateInput(target, true, changes);
                if (MOUSE.equals(name) && ((method.equals("onPress") && descriptor.equals("(JIII)V")) ||
                    ((method.equals("onScroll") || method.equals("onMove")) && descriptor.equals("(JDD)V"))))
                    return gateInput(target, false, changes);
                if (DISPLAY.equals(name) && method.equals("initWindow") && descriptor.equals("(Ljava/lang/String;)V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                            if (opcode == Opcodes.INVOKESTATIC && owner.equals("org/lwjgl/glfw/GLFW") &&
                                invoked.equals("glfwShowWindow") && type.equals("(J)V")) {
                                super.visitInsn(Opcodes.DUP2);
                                super.visitLdcInsn(Type.getObjectType(DISPLAY));
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "earlyWindowCreated", "(JLjava/lang/Class;)V", false);
                                changes[0]++;
                            }
                            super.visitMethodInsn(opcode, owner, invoked, type, iface);
                        }
                    };
                }
                if (WINDOW.equals(name) && method.equals("<init>") && descriptor.equals(
                    "(Lcom/mojang/blaze3d/platform/WindowEventHandler;Lcom/mojang/blaze3d/platform/ScreenManager;" +
                    "Lcom/mojang/blaze3d/platform/DisplayData;Ljava/lang/String;Ljava/lang/String;)V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                            super.visitMethodInsn(opcode, owner, invoked, type, iface);
                            if (opcode == Opcodes.INVOKESTATIC && owner.equals("net/minecraftforge/fml/loading/ImmediateWindowHandler") &&
                                invoked.equals("setupMinecraftWindow") && type.equals("(Ljava/util/function/IntSupplier;" +
                                    "Ljava/util/function/IntSupplier;Ljava/util/function/Supplier;Ljava/util/function/LongSupplier;)J")) {
                                // Confirm that Minecraft has inherited the early display's window.
                                super.visitInsn(Opcodes.DUP2);
                                super.visitLdcInsn(Type.getObjectType(WINDOW));
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "confirmWindow", "(JLjava/lang/Class;)V", false);
                                changes[0]++;
                            }
                        }
                    };
                }
                if (LISTENER.equals(name) && method.equals("setGain") && descriptor.equals("(F)V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                            if (opcode == Opcodes.INVOKESTATIC && owner.equals("org/lwjgl/openal/AL10") &&
                                invoked.equals("alListenerf") && type.equals("(IF)V")) {
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "filterGain", "(F)F", false);
                                changes[0]++;
                            }
                            super.visitMethodInsn(opcode, owner, invoked, type, iface);
                        }
                    };
                }
                if (MINECRAFT.equals(name) && method.equals("abortResourcePackRecovery") && descriptor.equals("()V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitCode() {
                            super.visitCode();
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "incompatible", "()V", false);
                            changes[0]++;
                        }
                    };
                }
                if (MINECRAFT.equals(name) && method.equals("runTick") && descriptor.equals("(Z)V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                            super.visitMethodInsn(opcode, owner, invoked, type, iface);
                            if (opcode == Opcodes.INVOKEVIRTUAL && WINDOW.equals(owner) &&
                                invoked.equals("updateDisplay") && type.equals("()V")) {
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, MINECRAFT, "gameLoadFinished", "Z");
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, MINECRAFT, "overlay", "Lnet/minecraft/client/gui/screens/Overlay;");
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, MINECRAFT, "screen", "Lnet/minecraft/client/gui/screens/Screen;");
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "framePresented", "(ZLjava/lang/Object;Ljava/lang/Object;)V", false);
                                changes[0]++;
                            }
                        }
                    };
                }
                return target;
            }
        }, 0);
        int expectedChanges = MOUSE.equals(name) ? 3 : MINECRAFT.equals(name) ? 2 : 1;
        return changes[0] == expectedChanges ? writer.toByteArray() : null;
    }

    private static MethodVisitor gateInput(MethodVisitor target, boolean keyboard, int[] changes) {
        return new MethodVisitor(Opcodes.ASM9, target) {
            @Override
            public void visitCode() {
                super.visitCode();
                Label gameInput = new Label();
                if (keyboard) {
                    super.visitVarInsn(Opcodes.ILOAD, 3); // GLFW key
                    super.visitVarInsn(Opcodes.ILOAD, 5); // GLFW action
                    super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "suppressKey", "(II)Z", false);
                } else {
                    super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "suppressPointer", "()Z", false);
                }
                super.visitJumpInsn(Opcodes.IFEQ, gameInput);
                super.visitInsn(Opcodes.RETURN);
                super.visitLabel(gameInput);
                super.visitFrame(Opcodes.F_SAME, 0, null, 0, null);
                changes[0]++;
            }
        };
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

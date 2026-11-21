package org.pcl3.intro;

import java.lang.instrument.ClassFileTransformer;
import java.security.MessageDigest;
import java.security.ProtectionDomain;
import java.util.Collections;
import java.util.HashMap;
import java.util.Map;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Label;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;

/** Exact profile for the official Java 26.3 client (SDL3 and RenderPearl). */
final class Vanilla263 implements ClassFileTransformer {
    private static final int ASM = Opcodes.ASM9;
    private static final String HOOKS = "org/pcl3/intro/IntroHooks";
    private static final String WINDOW = "com/mojang/blaze3d/platform/Window";
    private static final String GAME = "net/minecraft/client/Minecraft";
    private static final String GUI = "net/minecraft/client/gui/Gui";
    private static final String KEYBOARD = "net/minecraft/client/KeyboardHandler";
    private static final String KEY = "net/minecraft/client/input/KeyEvent";
    private static final String MOUSE = "net/minecraft/client/MouseHandler";
    private static final String AUDIO = "com/mojang/blaze3d/audio/Library";
    static final Map<String, String> TARGETS;
    static {
        Map<String, String> targets = new HashMap<>();
        targets.put(WINDOW, "bdd9296a3d6268826a71f94b306c93a5d7d92ae28887e6b6a0e7470a0096ae68");
        targets.put(GAME, "6620185007eb572e953c5fdad1e6e369b75b058b9dcd9a0bda9f8bc3f212c9d6");
        targets.put(KEYBOARD, "9c35e6723b9a6bfa857a87ec400e3e5c2119ea4cb4e907eb4ec8b17ec8e55d03");
        targets.put(MOUSE, "3151e8826e8a8819521f364ef53c8c11bb7c2158b31c1a3b99a0aa2214b20071");
        targets.put(AUDIO, "f43c1a6564f3ead966b626f30568815d11ffaf679a65d8ccd6ff53f2e5d4c78c");
        TARGETS = Collections.unmodifiableMap(targets);
    }

    @Override public byte[] transform(ClassLoader loader, String name, Class<?> redefined, ProtectionDomain domain, byte[] bytes) {
        if (loader == null || redefined != null || !TARGETS.containsKey(name)) return null;
        try {
            byte[] transformed = patch(name, bytes);
            if (transformed == null) IntroHooks.incompatible();
            else if (GAME.equals(name)) IntroHooks.firstFrameHookInstalled();
            return transformed;
        } catch (Exception | LinkageError failure) {
            IntroHooks.incompatible();
            return null;
        }
    }

    static byte[] patch(String name, byte[] bytes) throws Exception {
        String digest = TARGETS.get(name);
        if (digest == null) return null;
        byte[] expected = new byte[32];
        for (int i = 0; i < expected.length; i++) expected[i] = (byte) Integer.parseInt(digest.substring(2 * i, 2 * i + 2), 16);
        if (!MessageDigest.isEqual(expected, MessageDigest.getInstance("SHA-256").digest(bytes))) return null;
        ClassReader reader = new ClassReader(bytes);
        ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
        int[] changes = {0};
        reader.accept(new ClassVisitor(ASM, writer) {
            @Override public MethodVisitor visitMethod(int access, String method, String desc, String sig, String[] exceptions) {
                MethodVisitor target = super.visitMethod(access, method, desc, sig, exceptions);
                boolean keyPress = KEYBOARD.equals(name) && method.equals("keyPress") && desc.equals("(JIL" + KEY + ";)V");
                boolean pointerOrText = (KEYBOARD.equals(name) && method.equals("charTyped") && desc.equals("(JLnet/minecraft/client/input/CharacterEvent;)V")) ||
                    (MOUSE.equals(name) && (method.equals("onButton") && desc.equals("(JLnet/minecraft/client/input/MouseButtonInfo;I)V") ||
                    method.equals("onMove") && desc.equals("(JDDDD)V") || method.equals("onScroll") && desc.equals("(JDD)V")));
                if (keyPress || pointerOrText) return new MethodVisitor(ASM, target) {
                    @Override public void visitCode() {
                        super.visitCode();
                        Label vanilla = new Label();
                        if (keyPress) {
                            super.visitVarInsn(Opcodes.ALOAD, 4);
                            super.visitMethodInsn(Opcodes.INVOKEVIRTUAL, KEY, "keycode", "()I", false);
                            super.visitVarInsn(Opcodes.ILOAD, 3);
                            call("suppressKey", "(II)Z"); // SDL uses 1 for down, 0 for up and -1 for repeat.
                        } else call("suppressPointer", "()Z");
                        super.visitJumpInsn(Opcodes.IFEQ, vanilla);
                        super.visitInsn(Opcodes.RETURN);
                        super.visitLabel(vanilla);
                        super.visitFrame(Opcodes.F_SAME, 0, null, 0, null);
                        changes[0]++;
                    }
                    private void call(String hook, String descriptor) {
                        super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, hook, descriptor, false);
                    }
                };
                if (WINDOW.equals(name) && method.equals("<init>") && desc.equals("(Lcom/mojang/blaze3d/platform/WindowEventHandler;Lcom/mojang/blaze3d/platform/DisplayData;Ljava/lang/String;ZLjava/lang/String;Lcom/mojang/blaze3d/platform/MonitorManager;Lcom/mojang/renderpearl/api/device/GpuBackend;I)V")) {
                    return new MethodVisitor(ASM, target) {
                        @Override public void visitInsn(int opcode) {
                            if (opcode == Opcodes.RETURN) {
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, WINDOW, "handle", "J");
                                super.visitLdcInsn(Type.getObjectType(WINDOW));
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "sdlWindowCreated", "(JLjava/lang/Class;)V", false);
                                changes[0]++;
                            }
                            super.visitInsn(opcode);
                        }
                    };
                }
                if (AUDIO.equals(name) && method.equals("init") && desc.equals("(Ljava/lang/String;Lcom/mojang/blaze3d/audio/DeviceList;Z)V")) {
                    return new MethodVisitor(ASM, target) {
                        @Override public void visitInsn(int opcode) {
                            if (opcode == Opcodes.RETURN) {
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "muteNewListener", "()V", false);
                                changes[0]++;
                            }
                            super.visitInsn(opcode);
                        }
                    };
                }
                if (GAME.equals(name) && method.equals("renderFrame") && desc.equals("(Z)V")) {
                    return new MethodVisitor(ASM, target) {
                        @Override public void visitMethodInsn(int opcode, String owner, String method, String desc, boolean itf) {
                            boolean present = opcode == Opcodes.INVOKEINTERFACE && owner.equals("com/mojang/renderpearl/api/device/GpuSurface") && method.equals("present") && desc.equals("()V");
                            if (present) {
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, GAME, "window", "L" + WINDOW + ";");
                                super.visitMethodInsn(Opcodes.INVOKEVIRTUAL, WINDOW, "handle", "()J", false);
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "beforeFirstSwap", "(J)V", false);
                            }
                            super.visitMethodInsn(opcode, owner, method, desc, itf);
                            if (present) {
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitMethodInsn(Opcodes.INVOKEVIRTUAL, GAME, "isGameLoadFinished", "()Z", false);
                                guiValue("overlay", "Overlay");
                                guiValue("screen", "Screen");
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "framePresented", "(ZLjava/lang/Object;Ljava/lang/Object;)V", false);
                                changes[0]++;
                            }
                        }
                        private void guiValue(String method, String type) {
                            super.visitVarInsn(Opcodes.ALOAD, 0);
                            super.visitFieldInsn(Opcodes.GETFIELD, GAME, "gui", "L" + GUI + ";");
                            super.visitMethodInsn(Opcodes.INVOKEVIRTUAL, GUI, method, "()Lnet/minecraft/client/gui/screens/" + type + ";", false);
                        }
                    };
                }
                return target;
            }
        }, 0);
        return changes[0] == (MOUSE.equals(name) ? 3 : KEYBOARD.equals(name) ? 2 : 1) ? writer.toByteArray() : null;
    }
}

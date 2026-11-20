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

/** Exact post-Mixin profile for Minecraft 1.21.1 under Fabric Loader 0.16.14. */
final class Fabric1211 implements ClassFileTransformer {
    private static final String HOOKS = "org/pcl3/intro/IntroHooks";
    private static final Map<String, String> TARGETS = new HashMap<>();
    static {
        TARGETS.put("net/minecraft/class_310", "a93009faf016c62ac63ed0815f81b21c4bf5f5ae936cc9d3ed3ad8fb37eadb41");
        TARGETS.put("net/minecraft/class_309", "e3f9ce31ec14983f659c43cbf1abda67947d43e73ec9867147d550075475e7b2");
        TARGETS.put("net/minecraft/class_4227", "7edb4eec040a0abc809a958749577ed5e5e222e5e4dc906823b885c4d7ab7f17");
        TARGETS.put("net/minecraft/class_1041", "0b32eaf26f5e526cf3f4fdbb0efd09876629e2d620c63ef9c12969ab83dfad67");
        TARGETS.put("net/minecraft/class_312", "a34aa7d1e866285414d4bd4c90580dd25cd5c4c022deaba96c1d1e409a21870a");
    }

    @Override
    public byte[] transform(ClassLoader loader, String name, Class<?> redefined, ProtectionDomain domain, byte[] bytes) {
        if (!TARGETS.containsKey(name) || loader == null ||
            !loader.getClass().getName().equals("net.fabricmc.loader.impl.launch.knot.KnotClassLoader")) return null;
        try {
            byte[] transformed = patch(name, bytes);
            if (transformed != null && Class.forName(HOOKS.replace('/', '.'), false, loader) == IntroHooks.class)
                return transformed;
        } catch (Exception | LinkageError failure) {
            // Leave the class untouched if Knot cannot resolve the bootstrap bridge.
        }
        IntroHooks.incompatible();
        return null;
    }

    static byte[] patch(String name, byte[] bytes) throws Exception {
        if (!TARGETS.containsKey(name) || !fingerprint(bytes).equals(TARGETS.get(name))) return null;
        ClassReader reader = new ClassReader(bytes);
        ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
        int[] changes = {0};
        reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
            @Override
            public MethodVisitor visitMethod(int access, String method, String descriptor, String signature, String[] exceptions) {
                MethodVisitor target = super.visitMethod(access, method, descriptor, signature, exceptions);
                if (name.equals("net/minecraft/class_309") && method.equals("method_1466") && descriptor.equals("(JIIII)V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitCode() {
                            super.visitCode();
                            Label runVanilla = new Label();
                            super.visitVarInsn(Opcodes.ILOAD, 3); // GLFW key
                            super.visitVarInsn(Opcodes.ILOAD, 5); // GLFW action
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "suppressKey", "(II)Z", false);
                            super.visitJumpInsn(Opcodes.IFEQ, runVanilla);
                            super.visitInsn(Opcodes.RETURN);
                            super.visitLabel(runVanilla);
                            super.visitFrame(Opcodes.F_SAME, 0, null, 0, null);
                            changes[0]++;
                        }
                    };
                }
                if (name.equals("net/minecraft/class_312") && (descriptor.equals("(JIII)V") && method.equals("method_1601") ||
                    descriptor.equals("(JDD)V") && (method.equals("method_1598") || method.equals("method_1600")))) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override public void visitCode() {
                            super.visitCode();
                            Label runVanilla = new Label();
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "suppressPointer", "()Z", false);
                            super.visitJumpInsn(Opcodes.IFEQ, runVanilla);
                            super.visitInsn(Opcodes.RETURN);
                            super.visitLabel(runVanilla);
                            super.visitFrame(Opcodes.F_SAME, 0, null, 0, null);
                            changes[0]++;
                        }
                    };
                }
                if (name.equals("net/minecraft/class_4227") && method.equals("method_19670") && descriptor.equals("(F)V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                            if (opcode == Opcodes.INVOKESTATIC && owner.equals("org/lwjgl/openal/AL10") && invoked.equals("alListenerf") && type.equals("(IF)V")) {
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "filterGain", "(F)F", false);
                                changes[0]++;
                            }
                            super.visitMethodInsn(opcode, owner, invoked, type, iface);
                        }
                    };
                }
                if (name.equals("net/minecraft/class_1041") && method.equals("<init>") &&
                    descriptor.equals("(Lnet/minecraft/class_3678;Lnet/minecraft/class_323;Lnet/minecraft/class_543;Ljava/lang/String;Ljava/lang/String;)V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                            super.visitMethodInsn(opcode, owner, invoked, type, iface);
                            if (opcode == Opcodes.INVOKESTATIC && owner.equals("org/lwjgl/glfw/GLFW") && invoked.equals("glfwCreateWindow") && type.equals("(IILjava/lang/CharSequence;JJ)J")) {
                                super.visitInsn(Opcodes.DUP2);
                                super.visitLdcInsn(org.objectweb.asm.Type.getObjectType("net/minecraft/class_1041"));
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "windowCreated", "(JLjava/lang/Class;)V", false);
                                changes[0]++;
                            }
                        }
                    };
                }
                if (name.equals("net/minecraft/class_310") && method.equals("method_49294") && descriptor.equals("()V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override public void visitCode() {
                            super.visitCode();
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "incompatible", "()V", false);
                            changes[0]++;
                        }
                    };
                }
                if (name.equals("net/minecraft/class_310") && method.equals("method_1523") && descriptor.equals("(Z)V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                            super.visitMethodInsn(opcode, owner, invoked, type, iface);
                            if (opcode == Opcodes.INVOKEVIRTUAL && owner.equals("net/minecraft/class_1041") && invoked.equals("method_15998") && type.equals("()V")) {
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, "net/minecraft/class_310", "field_45900", "Z"); // successful initial load
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, "net/minecraft/class_310", "field_18175", "Lnet/minecraft/class_4071;"); // overlay
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, "net/minecraft/class_310", "field_1755", "Lnet/minecraft/class_437;"); // current screen
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "framePresented", "(ZLjava/lang/Object;Ljava/lang/Object;)V", false);
                                changes[0]++;
                            }
                        }
                    };
                }
                return target;
            }
        }, 0);
        return changes[0] == (name.equals("net/minecraft/class_312") ? 3 : name.equals("net/minecraft/class_310") ? 2 : 1) ? writer.toByteArray() : null;
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

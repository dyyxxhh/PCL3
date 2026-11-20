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

/** Exact launch profile for the official, unmodified Java 1.21.1 OpenGL client. */
final class Vanilla1211 implements ClassFileTransformer {
    private static final String HOOKS = "org/pcl3/intro/IntroHooks";
    private static final Map<String, String> TARGETS = new HashMap<>();
    static {
        TARGETS.put("fgo", "81b000661c1cc805209d409115897067cfe44987cc452c43767a26f8468748eb");
        TARGETS.put("fgn", "478d4719e1065d84e9e740c6c08cd629d4f5e2e69d911e7ae8384d76051bcc7a");
        TARGETS.put("ezg", "b584dcacd26e86627cf327cbe313d40fed934ddf5321b99510f37e608711b72f");
        TARGETS.put("fam", "08d22580b99e9e1e4a45e05c3b02702174dd72252f5a2515480a6fd4517f4ff3");
        TARGETS.put("fgp", "57730a3772d4907b4638e7b75cef171f05537669bd7eb9be14e9d1705a776141");
    }

    @Override
    public byte[] transform(ClassLoader loader, String name, Class<?> redefined, ProtectionDomain domain, byte[] bytes) {
        if (!TARGETS.containsKey(name)) return null;
        try {
            byte[] transformed = patch(name, bytes);
            if (transformed == null) IntroHooks.incompatible();
            return transformed;
        } catch (Exception | LinkageError failure) {
            IntroHooks.incompatible();
            return null;
        }
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
                if (name.equals("fgn") && method.equals("a") && descriptor.equals("(JIIII)V")) {
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
                if (name.equals("fgp") && (descriptor.equals("(JIII)V") && method.equals("a") ||
                    descriptor.equals("(JDD)V") && (method.equals("a") || method.equals("b")))) {
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
                if (name.equals("ezg") && method.equals("a") && descriptor.equals("(F)V")) {
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
                if (name.equals("fam") && method.equals("<init>")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                            super.visitMethodInsn(opcode, owner, invoked, type, iface);
                            if (opcode == Opcodes.INVOKESTATIC && owner.equals("org/lwjgl/glfw/GLFW") && invoked.equals("glfwCreateWindow") && type.equals("(IILjava/lang/CharSequence;JJ)J")) {
                                super.visitInsn(Opcodes.DUP2);
                                super.visitLdcInsn(org.objectweb.asm.Type.getObjectType("fam"));
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "windowCreated", "(JLjava/lang/Class;)V", false);
                                changes[0]++;
                            }
                        }
                    };
                }
                if (name.equals("fgo") && method.equals("be") && descriptor.equals("()V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override public void visitCode() {
                            super.visitCode();
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "incompatible", "()V", false);
                            changes[0]++;
                        }
                    };
                }
                if (name.equals("fgo") && method.equals("c") && descriptor.equals("(Z)V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override
                        public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                            super.visitMethodInsn(opcode, owner, invoked, type, iface);
                            if (opcode == Opcodes.INVOKEVIRTUAL && owner.equals("fam") && invoked.equals("f") && type.equals("()V")) {
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, "fgo", "bv", "Z"); // successful initial load
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, "fgo", "aW", "Lfnx;"); // overlay
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitFieldInsn(Opcodes.GETFIELD, "fgo", "y", "Lfod;"); // current screen
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "framePresented", "(ZLjava/lang/Object;Ljava/lang/Object;)V", false);
                                changes[0]++;
                            }
                        }
                    };
                }
                return target;
            }
        }, 0);
        return changes[0] == (name.equals("fgp") ? 3 : name.equals("fgo") ? 2 : 1) ? writer.toByteArray() : null;
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

package org.pcl3.intro;

import java.lang.instrument.ClassFileTransformer;
import java.security.MessageDigest;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Label;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;

/** Exact launch profile for the official Java 1.21.11 client. */
final class Vanilla12111 implements ClassFileTransformer {
    private static final String HOOKS = "org/pcl3/intro/IntroHooks";
    private static final String MINECRAFT = "gfj", KEYBOARD = "gfi", MOUSE = "gfk", WINDOW = "fyk", AUDIO = "fwh";

    @Override
    public byte[] transform(ClassLoader loader, String name, Class<?> redefined, ProtectionDomain domain, byte[] bytes) {
        if (name == null || loader == null || redefined != null) return null;
        String fingerprint;
        int expected;
        switch (name) {
            case MINECRAFT: fingerprint = "71fba2db01dd147e07cf61c7f2afe720fffa80e25badc6e54ba35e0c7be557df"; expected = 2; break;
            case KEYBOARD: fingerprint = "f86f5cdc6cfe6db152b64729c505aa47dca11a579004cbe9da479fdcf2bb3b78"; expected = 2; break;
            case MOUSE: fingerprint = "393fee555cfcdaed1791d8fb346a67fe620ea0c41ca96e56008af18fd83f8d39"; expected = 3; break;
            case WINDOW: fingerprint = "a51b8f175a4c13af3d2798533120c2a7d70e307e33c038adfe3c51576bdb2577"; expected = 1; break;
            case AUDIO: fingerprint = "6a53c91234a2a0435205e424a4c2536d9ec0b4bb3b097fabca1865a9a4740d71"; expected = 1; break;
            default: return null;
        }
        try {
            if (matches(bytes, fingerprint) && Class.forName(HOOKS.replace('/', '.'), false, loader) == IntroHooks.class) {
                byte[] patched = patch(name, bytes, expected);
                if (patched != null) return patched;
            }
        } catch (Exception | LinkageError ignored) {
            // Any mismatch must leave game startup viable.
        }
        IntroHooks.incompatible();
        return null;
    }

    private static byte[] patch(String name, byte[] bytes, int expected) {
        ClassReader reader = new ClassReader(bytes);
        ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
        int[] changes = {0};
        reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
            @Override
            public MethodVisitor visitMethod(int access, String method, String descriptor, String signature, String[] exceptions) {
                MethodVisitor target = super.visitMethod(access, method, descriptor, signature, exceptions);
                boolean key = KEYBOARD.equals(name) && method.equals("a") && descriptor.equals("(JILgzb;)V");
                boolean character = KEYBOARD.equals(name) && method.equals("a") && descriptor.equals("(JLgyy;)V");
                boolean pointer = MOUSE.equals(name) && (method.equals("a") && descriptor.equals("(JLgzd;I)V") ||
                    descriptor.equals("(JDD)V") && (method.equals("a") || method.equals("b")));
                if (key || character || pointer) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override public void visitCode() {
                            super.visitCode();
                            Label resume = new Label();
                            if (key) {
                                super.visitVarInsn(Opcodes.ALOAD, 4);
                                super.visitMethodInsn(Opcodes.INVOKEVIRTUAL, "gzb", "t", "()I", false);
                                super.visitVarInsn(Opcodes.ILOAD, 3);
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "suppressKey", "(II)Z", false);
                            } else {
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "suppressPointer", "()Z", false);
                            }
                            super.visitJumpInsn(Opcodes.IFEQ, resume);
                            super.visitInsn(Opcodes.RETURN);
                            super.visitLabel(resume);
                            super.visitFrame(Opcodes.F_SAME, 0, null, 0, null);
                            changes[0]++;
                        }
                    };
                }
                if (AUDIO.equals(name) && method.equals("a") && descriptor.equals("(Ljava/lang/String;Z)V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override public void visitInsn(int opcode) {
                            if (opcode == Opcodes.RETURN) {
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "muteNewListener", "()V", false);
                                changes[0]++;
                            }
                            super.visitInsn(opcode);
                        }
                    };
                }
                if (MINECRAFT.equals(name) && method.equals("bo") && descriptor.equals("()V")) {
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override public void visitCode() {
                            super.visitCode();
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "incompatible", "()V", false);
                            changes[0]++;
                        }
                    };
                }
                boolean createWindow = WINDOW.equals(name) && method.equals("<init>") &&
                    descriptor.equals("(Lfyl;Lfyi;Lfxz;Ljava/lang/String;Ljava/lang/String;)V");
                boolean render = MINECRAFT.equals(name) && method.equals("d") && descriptor.equals("(Z)V");
                if (!createWindow && !render) return target;
                return new MethodVisitor(Opcodes.ASM9, target) {
                    @Override public void visitMethodInsn(int opcode, String owner, String invoked, String type, boolean iface) {
                        super.visitMethodInsn(opcode, owner, invoked, type, iface);
                        if (createWindow && opcode == Opcodes.INVOKESTATIC && owner.equals("org/lwjgl/glfw/GLFW") &&
                            invoked.equals("glfwCreateWindow") && type.equals("(IILjava/lang/CharSequence;JJ)J")) {
                            super.visitInsn(Opcodes.DUP2);
                            super.visitLdcInsn(Type.getObjectType(WINDOW));
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "windowCreated", "(JLjava/lang/Class;)V", false);
                            changes[0]++;
                        } else if (render && opcode == Opcodes.INVOKEVIRTUAL && owner.equals(WINDOW) && invoked.equals("a") && type.equals("(Lfwf;)V")) {
                            readField("bt", "Z");
                            readField("aW", "Lgrw;");
                            readField("x", "Lgsb;");
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOKS, "framePresented", "(ZLjava/lang/Object;Ljava/lang/Object;)V", false);
                            changes[0]++;
                        }
                    }

                    private void readField(String field, String type) {
                        super.visitVarInsn(Opcodes.ALOAD, 0);
                        super.visitFieldInsn(Opcodes.GETFIELD, MINECRAFT, field, type);
                    }
                };
            }
        }, 0);
        return changes[0] == expected ? writer.toByteArray() : null;
    }

    private static boolean matches(byte[] bytes, String expected) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(bytes);
        for (int i = 0; i < digest.length; i++) {
            if (Character.forDigit((digest[i] >>> 4) & 15, 16) != expected.charAt(i * 2) ||
                Character.forDigit(digest[i] & 15, 16) != expected.charAt(i * 2 + 1)) return false;
        }
        return true;
    }
}

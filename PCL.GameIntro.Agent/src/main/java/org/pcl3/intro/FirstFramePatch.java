package org.pcl3.intro;

import java.lang.instrument.ClassFileTransformer;
import java.security.MessageDigest;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

/** Places a one-shot gate before the first display buffer swap in the verified LWJGL 3.3.3 class. */
final class FirstFramePatch implements ClassFileTransformer {
    private static final String GLFW = "org/lwjgl/glfw/GLFW";
    private static final byte[] EXPECTED = expectedDigest();

    @Override
    public byte[] transform(ClassLoader loader, String name, Class<?> redefined, ProtectionDomain domain, byte[] bytes) {
        if (!GLFW.equals(name) || loader == null || redefined != null) return null;
        try {
            if (!MessageDigest.isEqual(EXPECTED, MessageDigest.getInstance("SHA-256").digest(bytes)) ||
                Class.forName("org.pcl3.intro.IntroHooks", false, loader) != IntroHooks.class) return null;
            ClassReader reader = new ClassReader(bytes);
            ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
            int[] matches = {0};
            reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
                @Override public MethodVisitor visitMethod(int access, String method, String descriptor, String signature, String[] exceptions) {
                    MethodVisitor target = super.visitMethod(access, method, descriptor, signature, exceptions);
                    if (!method.equals("glfwSwapBuffers") || !descriptor.equals("(J)V")) return target;
                    matches[0]++;
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override public void visitCode() {
                            super.visitCode();
                            super.visitVarInsn(Opcodes.LLOAD, 0);
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, "org/pcl3/intro/IntroHooks", "beforeFirstSwap", "(J)V", false);
                        }
                    };
                }
            }, 0);
            if (matches[0] != 1) return null;
            byte[] transformed = writer.toByteArray();
            IntroHooks.firstFrameHookInstalled();
            return transformed;
        } catch (Exception | LinkageError unavailable) {
            return null;
        }
    }

    private static byte[] expectedDigest() {
        String hex = "9ed75d9980c66e0df8138fbca9bf4623c18fedbab9599b16ac989a1e25e8a40f";
        byte[] digest = new byte[32];
        for (int i = 0; i < digest.length; i++)
            digest[i] = (byte) Integer.parseInt(hex.substring(i * 2, i * 2 + 2), 16);
        return digest;
    }
}

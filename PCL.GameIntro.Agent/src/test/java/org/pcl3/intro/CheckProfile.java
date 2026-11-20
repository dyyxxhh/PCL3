package org.pcl3.intro;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Paths;
import java.util.Map;
import java.util.jar.JarEntry;
import java.util.jar.JarFile;
import org.pcl3.intro.internal.asm.ClassReader;

public final class CheckProfile {
    private CheckProfile() { }

    public static void main(String[] args) throws Exception {
        if (args.length != 1) throw new IllegalArgumentException("Usage: CheckProfile <official-client.jar>");
        MethodAccess patcher = new MethodAccess();
        try (JarFile jar = new JarFile(args[0]);
             URLClassLoader gameClasses = new URLClassLoader(new URL[] {Paths.get(args[0]).toUri().toURL()}, CheckProfile.class.getClassLoader())) {
            for (Map.Entry<String, String> target : patcher.targets().entrySet()) {
                JarEntry entry = jar.getJarEntry(target.getKey() + ".class");
                if (entry == null) throw new AssertionError("Missing client class " + target.getKey());
                byte[] original;
                try (InputStream source = jar.getInputStream(entry)) { original = readAll(source); }
                byte[] transformed = patcher.patch(target.getKey(), original);
                if (transformed == null) throw new AssertionError("Verified client signature did not match " + target.getKey());
                ClassReader reader = new ClassReader(transformed);
                reader.accept(new org.pcl3.intro.internal.asm.ClassVisitor(org.pcl3.intro.internal.asm.Opcodes.ASM9) {}, 0);
                try (TransformedLoader loader = new TransformedLoader(gameClasses, target.getKey(), transformed)) {
                    Class<?> verified = loader.loadTransformed();
                    if (!verified.getName().replace('.', '/').equals(target.getKey()))
                        throw new AssertionError("Unexpected client class " + verified.getName());
                }
                if (patcher.patch(target.getKey(), new byte[] {0, 1, 2}) != null)
                    throw new AssertionError("Invalid or partial client input was accepted.");
                System.out.println(target.getKey() + " official-client fingerprint and class verifier: PASS (" + target.getValue() + ")");
            }
        }
    }

    private static byte[] readAll(InputStream input) throws Exception {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = input.read(buffer)) != -1) output.write(buffer, 0, read);
        return output.toByteArray();
    }

    private static final class MethodAccess {
        private final java.lang.reflect.Method targetMethod;
        MethodAccess() throws Exception {
            targetMethod = Vanilla1211.class.getDeclaredMethod("patch", String.class, byte[].class);
            targetMethod.setAccessible(true);
        }
        @SuppressWarnings("unchecked")
        Map<String, String> targets() throws Exception {
            java.lang.reflect.Field field = Vanilla1211.class.getDeclaredField("TARGETS");
            field.setAccessible(true);
            return (Map<String, String>) field.get(null);
        }
        byte[] patch(String name, byte[] bytes) throws Exception {
            return (byte[]) targetMethod.invoke(null, name, bytes);
        }
    }

    private static final class TransformedLoader extends ClassLoader implements AutoCloseable {
        private final String name;
        private final byte[] bytes;
        private final ClassLoader gameClasses;
        TransformedLoader(ClassLoader gameClasses, String name, byte[] bytes) {
            super(CheckProfile.class.getClassLoader());
            this.gameClasses = gameClasses;
            this.name = name.replace('/', '.');
            this.bytes = bytes;
        }
        @Override protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
            if (!name.equals(this.name)) return gameClasses.loadClass(name);
            synchronized (getClassLoadingLock(name)) {
                Class<?> loaded = findLoadedClass(name);
                if (loaded == null) loaded = defineClass(name, bytes, 0, bytes.length);
                if (resolve) resolveClass(loaded);
                return loaded;
            }
        }
        Class<?> loadTransformed() throws ClassNotFoundException { return loadClass(name, true); }
        @Override public void close() { }
    }
}

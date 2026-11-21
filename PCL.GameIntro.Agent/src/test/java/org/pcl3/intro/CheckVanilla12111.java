package org.pcl3.intro;

import java.lang.instrument.ClassFileTransformer;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.jar.JarFile;
import org.pcl3.intro.internal.asm.ClassReader;

/** Probes exact-byte acceptance and fail-open rejection against the official client. */
public final class CheckVanilla12111 {
    public static void main(String[] args) throws Exception {
        if (args.length != 2) throw new IllegalArgumentException("Usage: CheckVanilla12111 <official-client.jar> <transformed-classes-dir>");
        ClassFileTransformer transformer = (ClassFileTransformer) Class.forName("org.pcl3.intro.Vanilla12111").getDeclaredConstructor().newInstance();
        Path output = Paths.get(args[1]);
        Files.createDirectories(output);
        ClassLoader loader = CheckVanilla12111.class.getClassLoader();
        List<String> targets = new ArrayList<>();
        try (JarFile jar = new JarFile(args[0])) {
            for (String name : new String[] {"gfj", "gfi", "gfk", "fyk", "fwh"}) {
                byte[] original;
                try (java.io.InputStream input = jar.getInputStream(jar.getJarEntry(name + ".class"))) {
                    original = input.readAllBytes();
                }
                byte[] patched = transformer.transform(loader, name, null, null, original);
                if (patched == null || !new ClassReader(patched).getClassName().equals(name))
                    throw new AssertionError("Official class not patched: " + name);
                byte[] changed = original.clone();
                changed[changed.length - 1] ^= 1;
                if (transformer.transform(loader, name, null, null, changed) != null)
                    throw new AssertionError("Changed class not rejected: " + name);
                Files.write(output.resolve(name + ".class"), patched);
                targets.add(name);
            }
            if (transformer.transform(loader, "unrelated", null, null, new byte[0]) != null)
                throw new AssertionError("Unrelated class changed");
        }
        Collections.sort(targets);
        System.out.println("Exact 1.21.11 transformations accepted and changed bytes rejected: " + targets);
    }
}

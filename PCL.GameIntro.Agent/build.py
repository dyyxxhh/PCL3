#!/usr/bin/env python3
"""Build the Java 8-compatible agent and embed verified, shaded ASM 9.8."""
import hashlib
import io
import os
import pathlib
import subprocess
import sys
import tempfile
import zipfile

ASM_SHA256 = '876eab6a83daecad5ca67eb9fcabb063c97b5aeb8cf1fca7a989ecde17522051'
ASM_PACKAGE = b'org/objectweb/asm'
PRIVATE_PACKAGE = b'org/pcl3/intro/internal/asm'
SOURCE = pathlib.Path(__file__).resolve().parent / 'src' / 'main' / 'java'
ROOT = pathlib.Path(__file__).resolve().parent


def relocate(bytecode):
    """Relocate ASM UTF-8 constants without modifying unrelated string values."""
    original = io.BytesIO(bytecode)
    output = io.BytesIO()
    output.write(original.read(8))
    count = original.read(2)
    output.write(count)
    index = 1
    lengths = {3: 4, 4: 4, 5: 8, 6: 8, 7: 2, 8: 2, 9: 4, 10: 4, 11: 4,
               12: 4, 15: 3, 16: 2, 17: 4, 18: 4, 19: 2, 20: 2}
    while index < int.from_bytes(count, 'big'):
        tag_bytes = original.read(1)
        if len(tag_bytes) != 1:
            raise ValueError('Truncated class constant pool.')
        tag = tag_bytes[0]
        output.write(tag_bytes)
        if tag == 1:
            size_bytes = original.read(2)
            length = int.from_bytes(size_bytes, 'big')
            value = original.read(length).replace(ASM_PACKAGE, PRIVATE_PACKAGE)
            output.write(len(value).to_bytes(2, 'big'))
            output.write(value)
        elif tag in lengths:
            output.write(original.read(lengths[tag]))
        else:
            raise ValueError(f'Unsupported JVM constant pool entry {tag}.')
        index += 2 if tag in (5, 6) else 1
    output.write(original.read())
    return output.getvalue()


def build(asm_jar, destination):
    asm_jar = pathlib.Path(asm_jar).resolve()
    destination = pathlib.Path(destination).resolve()
    dependency = asm_jar.read_bytes()
    if hashlib.sha256(dependency).hexdigest() != ASM_SHA256:
        raise SystemExit('Pinned ASM 9.8 SHA-256 verification failed.')
    notice = ROOT / 'NOTICE-ASM.txt'
    if not notice.is_file():
        raise SystemExit('Missing the redistribution notice for the shaded ASM dependency.')
    sources = sorted(SOURCE.rglob('*.java'))
    if not sources:
        raise SystemExit('The Java agent source set is empty.')
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='pcl3-intro-agent-') as directory:
        classes = pathlib.Path(directory)
        subprocess.run(['javac', '--release', '8', '-encoding', 'UTF-8', '-classpath',
                        str(asm_jar), '-d', str(classes), *map(str, sources)], check=True)
        temporary = destination.with_name(f'{destination.name}.{os.getpid()}.tmp')
        try:
            with zipfile.ZipFile(io.BytesIO(dependency)) as asm, zipfile.ZipFile(temporary, 'w') as result:
                def put(name, value):
                    entry = zipfile.ZipInfo(name, (2025, 1, 1, 0, 0, 0))
                    entry.compress_type = zipfile.ZIP_DEFLATED
                    entry.external_attr = 0o100644 << 16
                    result.writestr(entry, value)

                put('META-INF/MANIFEST.MF', b'Manifest-Version: 1.0\r\nPremain-Class: org.pcl3.intro.IntroAgent\r\n\r\n')
                put('META-INF/NOTICE-ASM.txt', notice.read_bytes())
                for name in sorted(asm.namelist()):
                    if name.startswith('org/objectweb/asm/') and name.endswith('.class'):
                        private_name = name.replace(ASM_PACKAGE.decode(), PRIVATE_PACKAGE.decode())
                        put(private_name, relocate(asm.read(name)))
                for path in sorted(classes.rglob('*.class')):
                    put(path.relative_to(classes).as_posix(), relocate(path.read_bytes()))
            temporary.replace(destination)
        finally:
            temporary.unlink(missing_ok=True)
    digest = hashlib.sha256(destination.read_bytes()).hexdigest()
    destination.with_name(destination.name + ".sha256").write_text(digest, encoding="ascii")
    print(digest)


if __name__ == '__main__':
    if len(sys.argv) != 3:
        raise SystemExit('Usage: build.py <pinned asm-9.8.jar> <output agent.jar>')
    build(sys.argv[1], sys.argv[2])

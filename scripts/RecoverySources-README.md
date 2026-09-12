# Recovery component corresponding source

This archive accompanies the separately licensed PhotoRec recovery program distributed with Shunshou Toolbox. PhotoRec is part of the TestDisk source distribution; the separate TestDisk executable is not included in the toolbox. This archive contains actual sources, their downstream patches and build recipes, the original license notices, and the locked download/preparation scripts. The large source archives are not needed to run the application.

## Contents

- `archives/`: unmodified TestDisk 7.2 source; Cygwin source packages (including upstream sources, Cygport build recipes and patches); full SRPMs for retained libewf and statically linked e2fsprogs/ntfsprogs (including original sources, RPM specs and patches).
- `recipes/`: convenient copies of those upstream build recipes and patches. The authoritative complete copies remain inside the original archives.
- `licenses/`: original upstream license and copyright notice files.
- `build/recovery-runtime-lock.json`: exact versions, official package-to-source mapping, URLs, SHA-256/SHA-512 and the six replacement DLL hashes. The retained libewf DLL is identified separately and was compared byte-for-byte with its source-associated COPR binary RPM.
- `build/`: the source preparation/packaging scripts used by the distributor. They read archives without executing third-party package installation scripts. Python 3.14 or later is required only on the build computer for standard-library Zstandard support.
- `SOURCE-MANIFEST.json`: SHA-256 and length for every included file except the manifest itself. ZIP entries use a fixed timestamp and sorted names for deterministic packaging of the same inputs.

## Build and modification notes

The PhotoRec executable is the unmodified CGSecurity 7.2 Windows x64 CLI binary. Its matching full TestDisk source archive includes `configure`, `Makefile` inputs, `compile.sh`, and installation instructions. This distribution replaces six original dynamic libraries with the officially packaged Cygwin x64 versions listed in the lock file, and supplies the matching official `terminfo` package's `63/cygwin` terminal definition. It retains the matching libewf 20140608 DLL and includes sources for static ext2fs 1.45.3 and ntfsprogs 2.0.0 code as well as the full PhotoRec/TestDisk program source.

To rebuild an individual Cygwin dynamic library, unpack its `*-src.tar.*` package and use its included `.cygport` recipe, upstream archive and patches in a Cygwin development environment with the recipe's declared build dependencies. To rebuild the older libewf/ext2fs/ntfs libraries, unpack the corresponding SRPM and follow its `.spec` preparation/configure/make steps (including every listed patch) in the cross-compilation environment described by CGSecurity. The copied recipes make those steps inspectable without installing the SRPM.

To rebuild TestDisk/PhotoRec, unpack `testdisk-7.2.tar.bz2`, build the required libraries using the supplied sources and recipes, and follow its `INSTALL`/`compile.sh` instructions for the `x86_64-pc-cygwin` target. The generic `compile.sh` defaults must be adjusted to the supplied dependency versions (in particular ext2fs 1.45.3). The distributor has supplied sources and preparation scripts; this archive does not claim byte-identical reproduction of CGSecurity's original executables or that a different compiler/library build has already passed the distributor's compatibility tests.

The application communicates with these independent programs through ordinary command-line options, process output and files. Users retain the copying, modification and redistribution rights provided by each recovery component's original license. Charging for the toolbox does not change those rights. The toolbox's other programs are separately licensed.

Official references:

- https://www.cgsecurity.org/testdisk_doc/compilation.html
- https://www.cgsecurity.org/testdisk_doc/crosscompilation_env.html
- https://cygwin.com/licensing.html
- https://www.sourceware.org/cygwin/packaging-package-files.html

Distribute this source archive from the same download location as the corresponding binary package, with equivalent access. Keep the exact source companion available with every binary release; merely referring recipients to upstream source websites is not this source-delivery method.

DOS Defrag Pixel for Windows — v8
=================================

INSTALL
1. Right-click the ZIP and choose Extract All.
2. Open the extracted folder.
3. Double-click RUN_BUILD_AND_INSTALL.cmd.
4. Use TEST_FULL_SCREEN.cmd to test immediately.

V8 BEHAVIOR CHANGES
- Initial fragmentation is built from short file-like runs and gaps rather than independent random blocks.
- Fragmentation remains visible in the first three rows and throughout the lower half.
- Read-source selection keeps the v6/v7 physical bottom sweep, nearby-run reads, and FAT-style jumps.
- Yellow completion has one strict row-major frontier.
- Yellow starts at the upper-left, moves left-to-right, then continues on the next row.
- X and B positions are skipped and never colored yellow.
- A block becomes yellow only after its own Reading / Writing / Updating FAT operation completes.
- No disconnected yellow blocks and no batch yellow reveals.
- Footer phrases switch directly in normal DOS red; there is no blanking, precharge, dissolve, or color burst.
- Full-screen mode is borderless and fills every monitor; all monitors share one synchronized simulation.

FILES
DOSDefragPixel.scr          Prebuilt native Windows screen saver
RUN_BUILD_AND_INSTALL.cmd   Per-user installer; no administrator rights required
TEST_FULL_SCREEN.cmd        Immediate full-screen test
RUN_LOGIC_TESTS.cmd         Runs 1,000 deterministic invariant tests
UNINSTALL.cmd               Removes the per-user installation
Source\                     C# source and build notes

EXIT
Move the mouse, click, or press a key.

The screen saver holds the completed layout for 2–5 minutes, then creates a new fragmented drive.

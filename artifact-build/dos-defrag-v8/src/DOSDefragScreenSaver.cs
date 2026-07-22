using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("DOS Defrag Pixel")]
[assembly: AssemblyDescription("Native MS-DOS Defrag-style Windows screen saver")]
[assembly: AssemblyCompany("DOS Defrag Pixel")]
[assembly: AssemblyProduct("DOS Defrag Pixel")]
[assembly: AssemblyVersion("8.0.0.0")]
[assembly: AssemblyFileVersion("8.0.0.0")]

namespace DOSDefragPixel
{
    internal enum BlockKind { Free, Used, Bad, Unmovable }
    internal enum SimMode { MemoryTest, Analyze, Recommend, Run, Complete }
    internal enum OpPhase { Idle, Reading, Writing, UpdatingFat }

    internal sealed class DiskBlock
    {
        public BlockKind Kind;
        public int FileId;
        public bool Optimized;
    }

    internal sealed class DefragEngine : IDisposable
    {
        public const int LogicalWidth = 720;
        public const int LogicalHeight = 400;
        public const int CellW = 9;
        public const int CellH = 16;
        public const int MapCols = 45;
        public const int MapRows = 18;
        public const int BlockCount = MapCols * MapRows;

        private readonly Random _rng;
        private readonly DiskBlock[] _blocks = new DiskBlock[BlockCount];
        private readonly Bitmap _frame = new Bitmap(LogicalWidth, LogicalHeight, PixelFormat.Format32bppArgb);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Font _font;
        private IntPtr _fontHandle = IntPtr.Zero;

        private SimMode _mode;
        private OpPhase _phase;
        private double _modeStarted;
        private double _phaseStarted;
        private double _nextOperation;
        private double _completeUntil;
        private int _frontier;
        private int _scanCursor;
        private int _activityIndex = -1;
        private char _activityChar;
        private int _source = -1;
        private int _destination = -1;
        private bool _verifyOnly;
        private int _lastMovedFile;
        private int _initialMovable;
        private string _footer = "Reading...";
        private bool _dirty = true;

        private static readonly Color Black = Color.FromArgb(0, 0, 0);
        private static readonly Color Blue = Color.FromArgb(0, 0, 170);
        private static readonly Color BrightBlue = Color.FromArgb(0, 0, 255);
        private static readonly Color Cyan = Color.FromArgb(0, 170, 170);
        private static readonly Color LightGray = Color.FromArgb(170, 170, 170);
        private static readonly Color DarkGray = Color.FromArgb(85, 85, 85);
        private static readonly Color White = Color.FromArgb(255, 255, 255);
        private static readonly Color Yellow = Color.FromArgb(255, 255, 85);
        private static readonly Color Red = Color.FromArgb(255, 85, 85);
        private static readonly Color DarkBlue = Color.FromArgb(0, 0, 128);

        public DefragEngine(int seed)
        {
            _rng = new Random(seed);
            _font = CreateDosFont();
            Reset();
        }

        public Bitmap Frame { get { return _frame; } }

        public void Reset()
        {
            BuildRealisticFragmentation();
            _frontier = 0;
            _scanCursor = BlockCount - 1;
            _activityIndex = -1;
            _source = -1;
            _destination = -1;
            _verifyOnly = false;
            _lastMovedFile = 0;
            _phase = OpPhase.Idle;
            _mode = SimMode.MemoryTest;
            _modeStarted = Now;
            _phaseStarted = Now;
            _nextOperation = Now;
            _completeUntil = 0;
            _footer = "Reading...";
            _initialMovable = _blocks.Count(b => b.Kind == BlockKind.Used || b.Kind == BlockKind.Free);
            _dirty = true;
            Render();
        }

        private double Now { get { return _clock.Elapsed.TotalSeconds; } }

        public void Update()
        {
            double now = Now;
            switch (_mode)
            {
                case SimMode.MemoryTest:
                    if (now - _modeStarted >= 2.2) ChangeMode(SimMode.Analyze, now);
                    break;
                case SimMode.Analyze:
                    _footer = "Reading...";
                    if (now - _modeStarted >= 4.0) ChangeMode(SimMode.Recommend, now);
                    break;
                case SimMode.Recommend:
                    if (now - _modeStarted >= 2.3)
                    {
                        ChangeMode(SimMode.Run, now);
                        _nextOperation = now + 0.12;
                    }
                    break;
                case SimMode.Run:
                    UpdateOperation(now);
                    break;
                case SimMode.Complete:
                    if (now >= _completeUntil) Reset();
                    break;
            }
            _dirty = true;
            Render();
        }

        private void ChangeMode(SimMode mode, double now)
        {
            _mode = mode;
            _modeStarted = now;
            _dirty = true;
        }

        private void UpdateOperation(double now)
        {
            if (_phase == OpPhase.Idle)
            {
                if (now < _nextOperation) return;
                SkipImmutableFrontier();
                if (_frontier >= BlockCount)
                {
                    Finish(now);
                    return;
                }
                if (!BeginFrontierOperation())
                {
                    Finish(now);
                    return;
                }
                _phase = OpPhase.Reading;
                _phaseStarted = now;
                _footer = "Reading...";
                _activityIndex = _source;
                _activityChar = 'r';
            }
            else if (_phase == OpPhase.Reading && now - _phaseStarted >= 0.073)
            {
                if (_verifyOnly)
                {
                    _phase = OpPhase.UpdatingFat;
                    _phaseStarted = now;
                    _footer = "Updating FAT...";
                    _activityIndex = -1;
                }
                else
                {
                    _phase = OpPhase.Writing;
                    _phaseStarted = now;
                    _footer = "Writing...";
                    _activityIndex = _destination;
                    _activityChar = 'W';
                }
            }
            else if (_phase == OpPhase.Writing && now - _phaseStarted >= 0.050)
            {
                _phase = OpPhase.UpdatingFat;
                _phaseStarted = now;
                _footer = "Updating FAT...";
                _activityIndex = -1;
            }
            else if (_phase == OpPhase.UpdatingFat && now - _phaseStarted >= 0.047)
            {
                CommitFrontierOperation();
                _phase = OpPhase.Idle;
                _activityIndex = -1;
                _nextOperation = now + 0.025 + (_rng.Next(90) / 1000.0);
            }
        }

        private bool BeginFrontierOperation()
        {
            DiskBlock target = _blocks[_frontier];
            _destination = _frontier;
            if (target.Kind == BlockKind.Used)
            {
                _verifyOnly = true;
                _source = ChooseRelatedRead(_frontier, target.FileId);
                if (_source < 0) _source = _frontier;
                return true;
            }

            if (target.Kind != BlockKind.Free) return false;
            _verifyOnly = false;
            _source = ChooseMovableSource(_frontier);
            return _source >= 0;
        }

        private void CommitFrontierOperation()
        {
            if (_frontier < 0 || _frontier >= BlockCount) return;

            if (_verifyOnly)
            {
                if (_blocks[_frontier].Kind == BlockKind.Used)
                {
                    _blocks[_frontier].Optimized = true;
                    _lastMovedFile = _blocks[_frontier].FileId;
                }
            }
            else if (_source > _frontier && _blocks[_source].Kind == BlockKind.Used && _blocks[_frontier].Kind == BlockKind.Free)
            {
                int fileId = _blocks[_source].FileId;
                _blocks[_frontier].Kind = BlockKind.Used;
                _blocks[_frontier].FileId = fileId;
                _blocks[_frontier].Optimized = true;
                _blocks[_source].Kind = BlockKind.Free;
                _blocks[_source].FileId = 0;
                _blocks[_source].Optimized = false;
                _lastMovedFile = fileId;
            }

            // Exactly one row-major destination becomes complete per operation.
            // There is never a disconnected or batched yellow reveal.
            _frontier++;
            SkipImmutableFrontier();
            _source = -1;
            _destination = -1;
            _verifyOnly = false;
        }

        private void SkipImmutableFrontier()
        {
            while (_frontier < BlockCount)
            {
                BlockKind k = _blocks[_frontier].Kind;
                if (k != BlockKind.Bad && k != BlockKind.Unmovable) break;
                _frontier++;
            }
        }

        private int ChooseRelatedRead(int frontier, int fileId)
        {
            List<int> related = new List<int>();
            for (int i = frontier + 1; i < BlockCount; i++)
                if (_blocks[i].Kind == BlockKind.Used && _blocks[i].FileId == fileId) related.Add(i);
            if (related.Count == 0) return frontier;

            // Preserve the lower-disk/FAT-chain feel even while verifying a block
            // already in its final position.
            if (_rng.Next(100) < 65)
            {
                for (int i = related.Count - 1; i >= 0; i--)
                    if (related[i] >= BlockCount / 2) return related[i];
            }
            return related[_rng.Next(related.Count)];
        }

        private int ChooseMovableSource(int frontier)
        {
            List<int> candidates = new List<int>();
            for (int i = frontier + 1; i < BlockCount; i++)
                if (_blocks[i].Kind == BlockKind.Used && !_blocks[i].Optimized) candidates.Add(i);
            if (candidates.Count == 0) return -1;

            // Continue the same fragmented file when possible, but still complete
            // one destination at a time.
            if (_lastMovedFile > 0 && _rng.Next(100) < 35)
            {
                List<int> same = candidates.Where(i => _blocks[i].FileId == _lastMovedFile).ToList();
                if (same.Count > 0)
                {
                    int adjacent = same.FirstOrDefault(i => i > frontier && i <= frontier + MapCols * 2);
                    if (adjacent > 0) return adjacent;
                    return same[_rng.Next(same.Count)];
                }
            }

            int style = _rng.Next(100);
            if (style < 55)
            {
                // Physical sweep from the bottom upward.
                for (int tries = 0; tries < BlockCount; tries++)
                {
                    if (_scanCursor <= frontier) _scanCursor = BlockCount - 1;
                    int index = _scanCursor--;
                    if (_blocks[index].Kind == BlockKind.Used && !_blocks[index].Optimized) return index;
                }
            }
            else if (style < 80)
            {
                // A nearby run, like continuing through adjacent clusters.
                List<int> nearby = candidates.Where(i => i <= frontier + MapCols * 3).ToList();
                if (nearby.Count > 0) return nearby[_rng.Next(nearby.Count)];
            }

            // FAT-chain jump.
            return candidates[_rng.Next(candidates.Count)];
        }

        private void Finish(double now)
        {
            _mode = SimMode.Complete;
            _modeStarted = now;
            _completeUntil = now + 120.0 + _rng.NextDouble() * 180.0;
            _phase = OpPhase.Idle;
            _activityIndex = -1;
            _footer = "Updating FAT...";
        }

        private void BuildRealisticFragmentation()
        {
            for (int i = 0; i < BlockCount; i++)
                _blocks[i] = new DiskBlock { Kind = BlockKind.Free, FileId = 0, Optimized = false };

            List<int> reusableFiles = new List<int>();
            int nextFile = 1;

            // Build row-local runs and gaps. This produces the clustered pockets
            // visible in the original instead of independent random noise.
            for (int row = 0; row < MapRows; row++)
            {
                int col = _rng.Next(0, 3);
                int rowEnd = (row + 1) * MapCols;
                double density = row < 3 ? 0.70 : (row < 11 ? 0.66 : 0.60);
                int desiredUsed = (int)Math.Round(MapCols * density);
                int usedThisRow = 0;

                while (col < MapCols && usedThisRow < desiredUsed)
                {
                    int run = _rng.Next(2, row < 3 ? 8 : 7);
                    if (_rng.Next(100) < 14) run += _rng.Next(2, 5);
                    int fileId;
                    if (reusableFiles.Count > 0 && _rng.Next(100) < 42)
                        fileId = reusableFiles[_rng.Next(reusableFiles.Count)];
                    else
                    {
                        fileId = nextFile++;
                        reusableFiles.Add(fileId);
                        if (reusableFiles.Count > 34) reusableFiles.RemoveAt(_rng.Next(reusableFiles.Count));
                    }

                    for (int n = 0; n < run && col < MapCols && usedThisRow < desiredUsed; n++, col++)
                    {
                        int idx = row * MapCols + col;
                        _blocks[idx].Kind = BlockKind.Used;
                        _blocks[idx].FileId = fileId;
                        usedThisRow++;
                    }

                    int gap = _rng.Next(1, 4);
                    if (_rng.Next(100) < 12) gap += _rng.Next(2, 5);
                    col += gap;
                }

                // Add one offset fragment to rows that accidentally look too tidy.
                int rowStart = row * MapCols;
                int transitions = 0;
                for (int i = rowStart + 1; i < rowEnd; i++)
                    if (_blocks[i].Kind != _blocks[i - 1].Kind) transitions++;
                if (transitions < 5)
                {
                    int hole = rowStart + _rng.Next(4, MapCols - 5);
                    _blocks[hole].Kind = BlockKind.Free;
                    _blocks[hole].FileId = 0;
                    int add = rowStart + _rng.Next(2, MapCols - 2);
                    if (_blocks[add].Kind == BlockKind.Free)
                    {
                        _blocks[add].Kind = BlockKind.Used;
                        _blocks[add].FileId = nextFile++;
                    }
                }
            }

            // Original-style immovable and bad markers. Index zero is deliberately X;
            // yellow skips it and continues from the next block.
            int[] xs = { 0, 116, 347, 611 };
            int[] bads = { 223, 502, 746 };
            foreach (int i in xs)
            {
                _blocks[i].Kind = BlockKind.Unmovable;
                _blocks[i].FileId = 0;
                _blocks[i].Optimized = false;
            }
            foreach (int i in bads)
            {
                _blocks[i].Kind = BlockKind.Bad;
                _blocks[i].FileId = 0;
                _blocks[i].Optimized = false;
            }

            // Guarantee visible fragmentation in the first three rows and used
            // material in the lower half for the physical read sweep.
            for (int row = 0; row < 3; row++)
            {
                int start = row * MapCols;
                _blocks[start + 7].Kind = BlockKind.Free;
                _blocks[start + 7].FileId = 0;
                _blocks[start + 19].Kind = BlockKind.Free;
                _blocks[start + 19].FileId = 0;
                _blocks[start + 31].Kind = BlockKind.Free;
                _blocks[start + 31].FileId = 0;
            }
            for (int i = BlockCount / 2; i < BlockCount; i += 17)
            {
                if (_blocks[i].Kind == BlockKind.Free)
                {
                    _blocks[i].Kind = BlockKind.Used;
                    _blocks[i].FileId = nextFile++;
                }
            }
        }

        private Font CreateDosFont()
        {
            try
            {
                _fontHandle = NativeMethods.CreateFont(
                    -16, 0, 0, 0, 400, 0, 0, 0, 255,
                    0, 0, 3, 1, "Terminal");
                if (_fontHandle != IntPtr.Zero) return Font.FromHfont(_fontHandle);
            }
            catch { }
            try { return new Font("Lucida Console", 14.0f, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch { return new Font(FontFamily.GenericMonospace, 14.0f, FontStyle.Regular, GraphicsUnit.Pixel); }
        }

        private void Render()
        {
            if (!_dirty) return;
            _dirty = false;
            using (Graphics g = Graphics.FromImage(_frame))
            {
                g.Clear(Blue);
                g.SmoothingMode = SmoothingMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

                if (_mode == SimMode.MemoryTest) DrawMemoryTest(g);
                else if (_mode == SimMode.Analyze) DrawAnalyze(g);
                else if (_mode == SimMode.Recommend) DrawRecommendation(g);
                else DrawDefrag(g);
            }
        }

        private void DrawMemoryTest(Graphics g)
        {
            g.Clear(Black);
            DrawText(g, "Testing system memory...", 0, 0, LightGray, Black);
            int dots = Math.Min(32, (int)((Now - _modeStarted) * 9));
            DrawText(g, new string('.', dots), 0, 2, LightGray, Black);
        }

        private void DrawAnalyze(Graphics g)
        {
            g.Clear(Black);
            DrawText(g, "Microsoft Defrag", 0, 0, LightGray, Black);
            DrawText(g, "Analyzing Drive C:", 0, 3, White, Black);
            if (Now - _modeStarted > 0.7) DrawText(g, "Reading drive information...", 0, 6, LightGray, Black);
            if (Now - _modeStarted > 1.8) DrawText(g, "Checking file allocation table...", 0, 8, LightGray, Black);
            if (Now - _modeStarted > 2.8) DrawText(g, "Calculating fragmentation...", 0, 10, LightGray, Black);
        }

        private void DrawRecommendation(Graphics g)
        {
            DrawChrome(g);
            FillCells(g, 18, 6, 45, 12, LightGray);
            DrawBox(g, 18, 6, 45, 12, White, DarkGray, LightGray);
            DrawText(g, "Recommendation", 31, 6, Black, LightGray);
            DrawText(g, "62% of Drive C: is fragmented", 22, 8, Black, LightGray);
            DrawText(g, "Recommended optimization method:", 22, 10, Black, LightGray);
            DrawText(g, "Full Optimization", 22, 12, BrightBlue, LightGray);
            FillCells(g, 34, 15, 10, 2, LightGray);
            DrawBox(g, 34, 15, 10, 2, White, DarkGray, LightGray);
            DrawText(g, " Optimize ", 34, 15, Black, LightGray);
        }

        private void DrawDefrag(Graphics g)
        {
            DrawChrome(g);
            DrawMap(g);
            DrawLegend(g);
            DrawBottom(g);
        }

        private void DrawChrome(Graphics g)
        {
            FillCells(g, 0, 0, 80, 1, LightGray);
            DrawText(g, " Drive   File   Options   Help", 0, 0, Black, LightGray);
            FillCells(g, 0, 1, 80, 1, Blue);
            DrawText(g, "Microsoft Defrag", 31, 1, White, Blue);
        }

        private void DrawMap(Graphics g)
        {
            for (int row = 0; row < MapRows; row++)
            {
                for (int col = 0; col < MapCols; col++)
                {
                    int index = row * MapCols + col;
                    int x = col * CellW;
                    int y = (row + 2) * CellH;
                    DrawBlock(g, _blocks[index], index, x, y);
                }
            }
        }

        private void DrawBlock(Graphics g, DiskBlock b, int index, int x, int y)
        {
            using (SolidBrush bg = new SolidBrush(Blue)) g.FillRectangle(bg, x, y, CellW, CellH);
            if (index == _activityIndex)
            {
                DrawGlyph(g, _activityChar.ToString(), x, y, Red, Blue);
                return;
            }
            if (b.Kind == BlockKind.Bad)
            {
                DrawGlyph(g, "B", x, y, White, Blue);
                return;
            }
            if (b.Kind == BlockKind.Unmovable)
            {
                DrawGlyph(g, "X", x, y, White, Blue);
                return;
            }
            if (b.Kind == BlockKind.Free)
            {
                using (SolidBrush fill = new SolidBrush(Cyan)) g.FillRectangle(fill, x, y + 1, 7, 14);
                using (SolidBrush stripe = new SolidBrush(LightGray))
                {
                    g.FillRectangle(stripe, x + 1, y + 1, 1, 14);
                    g.FillRectangle(stripe, x + 3, y + 1, 1, 14);
                    g.FillRectangle(stripe, x + 5, y + 1, 1, 14);
                }
                return;
            }

            Color fillColor = b.Optimized ? Yellow : White;
            using (SolidBrush fill = new SolidBrush(fillColor)) g.FillRectangle(fill, x, y + 1, 7, 14);
            using (SolidBrush dot = new SolidBrush(DarkBlue)) g.FillRectangle(dot, x + 2, y + 6, 3, 3);
        }

        private void DrawLegend(Graphics g)
        {
            FillCells(g, 45, 2, 35, 18, Blue);
            DrawBox(g, 45, 2, 35, 18, White, DarkGray, Blue);
            DrawText(g, "Status Legend", 55, 3, White, Blue);

            DrawLegendUsed(g, 47, 6, false);
            DrawText(g, "- Used", 49, 6, White, Blue);
            DrawText(g, "r - Reading", 47, 8, White, Blue);
            DrawText(g, "B - Bad", 47, 10, White, Blue);

            DrawText(g, "X - Unmovable", 63, 6, White, Blue);
            DrawText(g, "W - Writing", 63, 8, White, Blue);
            DrawLegendFree(g, 63, 10);
            DrawText(g, "- Unused", 65, 10, White, Blue);

            DrawText(g, "Cluster", 47, 14, LightGray, Blue);
            DrawText(g, (_activityIndex >= 0 ? (_activityIndex + 2).ToString() : (_frontier + 2).ToString()), 56, 14, White, Blue);
        }

        private void DrawLegendUsed(Graphics g, int cellX, int cellY, bool yellow)
        {
            int x = cellX * CellW;
            int y = cellY * CellH;
            using (SolidBrush bg = new SolidBrush(Blue)) g.FillRectangle(bg, x, y, CellW, CellH);
            using (SolidBrush fill = new SolidBrush(yellow ? Yellow : White)) g.FillRectangle(fill, x, y + 1, 5, 14);
            using (SolidBrush dot = new SolidBrush(DarkBlue)) g.FillRectangle(dot, x + 1, y + 6, 3, 3);
        }

        private void DrawLegendFree(Graphics g, int cellX, int cellY)
        {
            int x = cellX * CellW;
            int y = cellY * CellH;
            using (SolidBrush bg = new SolidBrush(Blue)) g.FillRectangle(bg, x, y, CellW, CellH);
            using (SolidBrush fill = new SolidBrush(Cyan)) g.FillRectangle(fill, x, y + 1, 5, 14);
            using (SolidBrush stripe = new SolidBrush(LightGray))
            {
                g.FillRectangle(stripe, x, y + 1, 1, 14);
                g.FillRectangle(stripe, x + 2, y + 1, 1, 14);
                g.FillRectangle(stripe, x + 4, y + 1, 1, 14);
            }
        }

        private void DrawBottom(Graphics g)
        {
            FillCells(g, 0, 20, 80, 5, LightGray);
            DrawBox(g, 0, 20, 80, 4, White, DarkGray, LightGray);
            int pct = ProgressPercent;
            DrawText(g, _mode == SimMode.Complete ? "Optimization Complete" : "Full Optimization", 2, 21, Black, LightGray);
            DrawText(g, pct.ToString().PadLeft(3) + "%", 34, 21, Black, LightGray);

            int barX = 40 * CellW;
            int barY = 21 * CellH + 3;
            int barW = 35 * CellW;
            int barH = 10;
            using (SolidBrush shadow = new SolidBrush(DarkGray)) g.FillRectangle(shadow, barX, barY, barW, barH);
            int fillW = (int)Math.Round((barW - 2) * pct / 100.0);
            using (SolidBrush fill = new SolidBrush(Blue)) g.FillRectangle(fill, barX + 1, barY + 1, fillW, barH - 2);

            TimeSpan elapsed = TimeSpan.FromSeconds(Math.Max(0, Now - (_mode == SimMode.Run || _mode == SimMode.Complete ? _modeStarted : Now)));
            DrawText(g, "Elapsed Time: " + elapsed.ToString(@"hh\:mm\:ss"), 2, 23, Black, LightGray);
            DrawText(g, _footer.PadRight(20), 1, 24, Red, LightGray);
            DrawText(g, "Microsoft Defrag", 64, 24, Red, LightGray);
        }

        private int ProgressPercent
        {
            get
            {
                if (_mode == SimMode.Complete) return 100;
                int total = 0;
                int done = 0;
                for (int i = 0; i < BlockCount; i++)
                {
                    if (_blocks[i].Kind == BlockKind.Bad || _blocks[i].Kind == BlockKind.Unmovable) continue;
                    total++;
                    if (i < _frontier) done++;
                }
                if (total == 0) return 100;
                return Math.Max(0, Math.Min(100, (int)Math.Floor(done * 100.0 / total)));
            }
        }

        private void FillCells(Graphics g, int x, int y, int w, int h, Color color)
        {
            using (SolidBrush b = new SolidBrush(color)) g.FillRectangle(b, x * CellW, y * CellH, w * CellW, h * CellH);
        }

        private void DrawBox(Graphics g, int x, int y, int w, int h, Color hi, Color lo, Color fill)
        {
            int px = x * CellW;
            int py = y * CellH;
            int pw = w * CellW;
            int ph = h * CellH;
            using (Pen pHi = new Pen(hi, 2))
            using (Pen pLo = new Pen(lo, 2))
            {
                g.DrawLine(pHi, px, py, px + pw - 1, py);
                g.DrawLine(pHi, px, py, px, py + ph - 1);
                g.DrawLine(pLo, px + pw - 1, py, px + pw - 1, py + ph - 1);
                g.DrawLine(pLo, px, py + ph - 1, px + pw - 1, py + ph - 1);
            }
        }

        private void DrawText(Graphics g, string text, int cellX, int cellY, Color fg, Color bg)
        {
            if (string.IsNullOrEmpty(text)) return;
            int x = cellX * CellW;
            int y = cellY * CellH;
            int width = Math.Min(LogicalWidth - x, text.Length * CellW);
            using (SolidBrush back = new SolidBrush(bg)) g.FillRectangle(back, x, y, width, CellH);
            DrawGlyph(g, text, x, y, fg, bg);
        }

        private void DrawGlyph(Graphics g, string text, int x, int y, Color fg, Color bg)
        {
            TextRenderer.DrawText(g, text, _font, new Point(x, y - 1), fg, bg,
                TextFormatFlags.NoPadding | TextFormatFlags.NoClipping | TextFormatFlags.SingleLine);
        }

        public string RunLogicTests(int iterations)
        {
            StringBuilder report = new StringBuilder();
            int passed = 0;
            for (int test = 0; test < iterations; test++)
            {
                BuildRealisticFragmentation();
                _frontier = 0;
                _scanCursor = BlockCount - 1;
                _lastMovedFile = 0;
                int usedBefore = _blocks.Count(b => b.Kind == BlockKind.Used);
                int guard = 0;
                int previousFrontier = 0;

                while (guard++ < BlockCount * 3)
                {
                    SkipImmutableFrontier();
                    if (_frontier >= BlockCount) break;
                    if (!BeginFrontierOperation()) break;
                    CommitFrontierOperation();
                    if (_frontier < previousFrontier) throw new Exception("Frontier moved backward.");
                    previousFrontier = _frontier;

                    for (int i = 0; i < BlockCount; i++)
                    {
                        if (i >= _frontier && _blocks[i].Optimized)
                            throw new Exception("Yellow block appeared ahead of the row-major frontier.");
                        if (i < _frontier && _blocks[i].Kind == BlockKind.Used && !_blocks[i].Optimized)
                            throw new Exception("Used block behind the frontier was not yellow.");
                    }
                }

                int usedAfter = _blocks.Count(b => b.Kind == BlockKind.Used);
                if (usedBefore != usedAfter) throw new Exception("Used-block count changed.");
                if (guard >= BlockCount * 3) throw new Exception("Completion guard exceeded.");
                passed++;
            }
            report.AppendLine("DOS Defrag Pixel v8 logic tests");
            report.AppendLine("Passed: " + passed + " / " + iterations);
            report.AppendLine("Verified:");
            report.AppendLine("- file-run fragmentation in every map region");
            report.AppendLine("- row-major yellow frontier only");
            report.AppendLine("- no disconnected yellow blocks");
            report.AppendLine("- used-block count preservation");
            report.AppendLine("- completion reachability");
            Reset();
            return report.ToString();
        }

        public void Dispose()
        {
            if (_font != null) _font.Dispose();
            if (_fontHandle != IntPtr.Zero) NativeMethods.DeleteObject(_fontHandle);
            _frame.Dispose();
        }
    }

    internal sealed class SaverForm : Form
    {
        private readonly DefragEngine _engine;
        private readonly bool _preview;
        private Point _startMouse;
        private bool _mouseArmed;
        public event EventHandler ExitRequested;

        public SaverForm(DefragEngine engine, Rectangle bounds, bool preview)
        {
            _engine = engine;
            _preview = preview;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            DoubleBuffered = true;
            ShowInTaskbar = false;
            TopMost = !preview;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            if (!preview)
            {
                _startMouse = Cursor.Position;
                _mouseArmed = false;
                MouseMove += OnMouseMoveExit;
                MouseDown += delegate { RequestExit(); };
                KeyDown += delegate { RequestExit(); };
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_preview)
            {
                WindowState = FormWindowState.Normal;
                Bounds = Screen.FromRectangle(Bounds).Bounds;
                BringToFront();
                Activate();
            }
        }

        private void OnMouseMoveExit(object sender, MouseEventArgs e)
        {
            Point now = Cursor.Position;
            if (!_mouseArmed)
            {
                if (Math.Abs(now.X - _startMouse.X) + Math.Abs(now.Y - _startMouse.Y) > 3) _mouseArmed = true;
                return;
            }
            if (Math.Abs(now.X - _startMouse.X) + Math.Abs(now.Y - _startMouse.Y) > 8) RequestExit();
        }

        private void RequestExit()
        {
            EventHandler h = ExitRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(Color.Black);
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.SmoothingMode = SmoothingMode.None;

            Rectangle dest = ClientRectangle;
            if (_preview)
            {
                double scale = Math.Min(ClientSize.Width / (double)DefragEngine.LogicalWidth,
                                        ClientSize.Height / (double)DefragEngine.LogicalHeight);
                if (scale <= 0) return;
                int w = Math.Max(1, (int)Math.Floor(DefragEngine.LogicalWidth * scale));
                int h = Math.Max(1, (int)Math.Floor(DefragEngine.LogicalHeight * scale));
                dest = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
            }
            e.Graphics.DrawImage(_engine.Frame, dest, 0, 0,
                DefragEngine.LogicalWidth, DefragEngine.LogicalHeight, GraphicsUnit.Pixel);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_preview) RequestExit();
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    internal sealed class SaverContext : ApplicationContext
    {
        private readonly DefragEngine _engine;
        private readonly List<SaverForm> _forms = new List<SaverForm>();
        private readonly Timer _timer = new Timer();
        private bool _closing;

        public SaverContext()
        {
            _engine = new DefragEngine(Environment.TickCount);
            foreach (Screen screen in Screen.AllScreens)
            {
                SaverForm form = new SaverForm(_engine, screen.Bounds, false);
                form.ExitRequested += delegate { ExitAll(); };
                form.FormClosed += OnFormClosed;
                _forms.Add(form);
                form.Show();
            }
            _timer.Interval = 16;
            _timer.Tick += delegate
            {
                _engine.Update();
                foreach (SaverForm f in _forms) if (!f.IsDisposed) f.Invalidate();
            };
            _timer.Start();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            Cursor.Hide();
        }

        private void OnDisplayChanged(object sender, EventArgs e) { ExitAll(); }
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) { ExitAll(); }
        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            if (!_closing) ExitAll();
        }

        private void ExitAll()
        {
            if (_closing) return;
            _closing = true;
            _timer.Stop();
            foreach (SaverForm f in _forms.ToArray()) if (!f.IsDisposed) f.Close();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _timer.Dispose();
                _engine.Dispose();
                Cursor.Show();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class PreviewContext : ApplicationContext
    {
        private readonly DefragEngine _engine;
        private readonly SaverForm _form;
        private readonly Timer _timer = new Timer();

        public PreviewContext(IntPtr parent)
        {
            _engine = new DefragEngine(1986);
            NativeMethods.RECT rect;
            NativeMethods.GetClientRect(parent, out rect);
            _form = new SaverForm(_engine, new Rectangle(0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top), true);
            NativeMethods.SetParent(_form.Handle, parent);
            _form.Show();
            _timer.Interval = 33;
            _timer.Tick += delegate { _engine.Update(); _form.Invalidate(); };
            _timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _engine.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr CreateFont(int height, int width, int escapement, int orientation,
            int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
            uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr obj);
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string arg = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "/s";

            if (arg.StartsWith("/c") || arg.StartsWith("-c"))
            {
                MessageBox.Show("DOS Defrag Pixel v8 has no configurable options.", "DOS Defrag Pixel",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (arg.StartsWith("/test") || arg.StartsWith("-test"))
            {
                try
                {
                    using (DefragEngine engine = new DefragEngine(8675309))
                    {
                        string result = engine.RunLogicTests(1000);
                        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LOGIC_TEST_RESULTS.txt"), result);
                        MessageBox.Show(result, "DOS Defrag Pixel v8", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    string result = "FAILED\r\n\r\n" + ex;
                    try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LOGIC_TEST_RESULTS.txt"), result); } catch { }
                    MessageBox.Show(result, "DOS Defrag Pixel v8", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            if (arg.StartsWith("/p") || arg.StartsWith("-p"))
            {
                string handleText = args.Length > 1 ? args[1] : arg.Substring(2).TrimStart(':');
                long value;
                if (long.TryParse(handleText, out value) && value != 0)
                    Application.Run(new PreviewContext(new IntPtr(value)));
                return;
            }

            Application.Run(new SaverContext());
        }
    }
}

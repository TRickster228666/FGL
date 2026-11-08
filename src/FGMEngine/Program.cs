using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SharpGen.Runtime;
using MapFlags = Vortice.Direct3D11.MapFlags;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

// FrameGenLive v2:
//   Захват экрана (Desktop Duplication) -> Поиск движения на GPU (HLSL) -> Показ на оверлее поверх игры.
//   Вывод по такту монитора (с динамическим отслеживанием герцовки).
//   Поддержка экстраполяции векторов (--extrap) и сглаживания временных меток (--tsmooth).
//   Высокоточный мультимедийный таймер Windows (timeBeginPeriod).

static class Program
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] static extern uint TimeBeginPeriod(uint uMilliseconds);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]   static extern uint TimeEndPeriod(uint uMilliseconds);

    sealed class Options
    {
        public int Gpu = -1, Output = 0;
        public int Mode = 1, Median = 2, Subpix = 1;
        public int Levels = 4, R0 = 16, Rr = 2, Lambda = 20, Thresh = 20;
        public bool Flip = true;
        public bool Diag = true;
        public int Latency = 2;       // глубина очереди показа (1..3)
        public bool Prio = true;      // приоритет GPU-планировщика
        public double Lag = 0;        // доп. задержка показа (мс)
        public double JitGain = 2.0;
        public int Every = 0;         // показ каждый N-й такт (0 = авто: 1)
        public double Extrap = 1.5;   // потолок досчёта движения вперёд (1.0..1.8)
        public double TSmooth = 0;    // выравнивание временных меток кадра (0 = выкл, 0.15 для 30 FPS)
        public double Mult = 0;       // множитель FPS (0 = авто)
        public int TargetHz = 0;      // принудительная герцовка (0 = авто из Windows)
        public bool SelfCheck = false;
        public bool Bench = false;
        public bool NoStatic = false;
        public bool NoOcc = false;
        public bool NoFade = true;    // по умолчанию чистый варп без двоения полупрозрачных кадров
        public bool Tearing = false;  // принудительный tearing (без ожидания VSync)
        public bool Prev = false;
        public double HitchK = 2.5;   // порог отсечки рывков подачи
        public int Tune = 0;          // 0 = баланс, -1 = отзывчивее, +1 = ровнее
        public double Delay = -1;     // принудительный запас (мс), -1 = авто
    }

    static readonly string[] ModeNames = { "0: без генерации (настоящие кадры)", "1: генерация по векторам", "2: простое смешивание" };
    static volatile int mode = 1;
    static volatile bool quit = false;
    static volatile int mult = 0;

    static string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);

    static readonly object logLock = new object();
    static string logPath = null;
    static void Log(string s)
    {
        Console.WriteLine(s);
        try
        {
            lock (logLock)
            {
                if (logPath == null)
                {
                    logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "framegen_log.txt");
                    try
                    {
                        if (System.IO.File.Exists(logPath) && new System.IO.FileInfo(logPath).Length > 0)
                            System.IO.File.Move(logPath, System.IO.Path.Combine(AppContext.BaseDirectory, "framegen_log_prev.txt"), true);
                    }
                    catch { }
                }
                System.IO.File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ", CultureInfo.InvariantCulture) + s + "\n", Encoding.UTF8);
            }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("gdi32.dll")] static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr hProcess, int priority);

    static int CurrentHz(string deviceName)
    {
        try
        {
            var dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            return EnumDisplaySettings(deviceName, -1, ref dm) ? (int)dm.dmDisplayFrequency : 0;
        }
        catch { return 0; }
    }

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Options o;
        try { o = ParseArgs(args); }
        catch (Exception e) { Console.WriteLine(e.Message); return 1; }
        mode = o.Mode;
        if (o.SelfCheck) return SelfCheck();
        if (o.Bench) return Bench();

        // Включаем таймер высокого разрешения Windows (1 мс вместо 15.6 мс)
        TimeBeginPeriod(1);
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var adapters = new List<IDXGIAdapter1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 a).Success; i++) adapters.Add(a);
            Console.WriteLine("Видеокарты и мониторы:");
            for (int i = 0; i < adapters.Count; i++)
            {
                Console.WriteLine($"  [{i}] {adapters[i].Description1.Description}");
                for (uint oi = 0; adapters[i].EnumOutputs(oi, out IDXGIOutput outp).Success; oi++)
                {
                    var r = outp.Description.DesktopCoordinates;
                    Console.WriteLine($"        монитор {oi}: {outp.Description.DeviceName}  {r.Right - r.Left}x{r.Bottom - r.Top}  в точке ({r.Left},{r.Top})");
                    outp.Dispose();
                }
            }

            int gpu = o.Gpu;
            if (gpu < 0)
            {
                for (int i = 0; i < adapters.Count; i++)
                    if (adapters[i].EnumOutputs(0, out IDXGIOutput probe).Success) { probe.Dispose(); gpu = i; break; }
            }
            if (gpu < 0 || gpu >= adapters.Count)
            {
                Console.WriteLine("Не нашёл видеокарту с подключённым монитором. Укажите --gpu N.");
                foreach (var a in adapters) a.Dispose();
                return 1;
            }
            if (!adapters[gpu].EnumOutputs((uint)o.Output, out IDXGIOutput output).Success)
            {
                Console.WriteLine($"У видеокарты [{gpu}] нет монитора {o.Output}. Укажите верные --gpu и --output.");
                foreach (var a in adapters) a.Dispose();
                return 1;
            }

            // Освобождаем неиспользуемые адаптеры
            for (int i = 0; i < adapters.Count; i++)
                if (i != gpu) adapters[i].Dispose();

            var rc = output.Description.DesktopCoordinates;
            int W = rc.Right - rc.Left, H = rc.Bottom - rc.Top;
            Console.WriteLine($"Используется: [{gpu}] {adapters[gpu].Description1.Description}, монитор {o.Output}, {W}x{H}");
            Console.WriteLine($"Режим: {ModeNames[mode]}");
            Console.WriteLine("Ctrl+Alt+F8 - сменить режим, Ctrl+Alt+1..9 - множитель, Ctrl+Alt+0 - авто, Ctrl+Alt+F10 - выход.");
            Console.WriteLine();

            var bounds = new Rectangle(rc.Left, rc.Top, W, H);
            var form = new OverlayForm(bounds);
            form.OnToggle = () =>
            {
                mode = (mode + 1) % 3;
                Console.WriteLine($">>> режим {ModeNames[mode]}");
            };
            form.OnQuit = () => { quit = true; };
            form.OnMult = n =>
            {
                mult = n;
                Log(n == 0 ? ">>> вывод: авто (каждый такт монитора)" : $">>> вывод: каждый {n}-й такт монитора");
            };
            Console.CancelKeyPress += (s, e) => { quit = true; e.Cancel = true; };

            form.Shown += (s, e) =>
            {
                form.Bounds = bounds;
                IntPtr hwnd = form.Handle;
                var th = new Thread(() =>
                {
                    try { RunLive(o, adapters[gpu], output, hwnd, W, H); }
                    catch (Exception ex) { Console.WriteLine("Ошибка: " + ex); }
                    finally
                    {
                        output.Dispose();
                        adapters[gpu].Dispose();
                        try { form.BeginInvoke(new Action(form.Close)); } catch { }
                    }
                });
                th.IsBackground = true;
                th.Priority = ThreadPriority.Highest;
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }
                th.Start();
            };

            Application.Run(form);
            quit = true;
            return 0;
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    static int Bench()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
        using (dev)
        using (ctx)
        {
            using var pipe = new LivePipeline(dev, ctx, 1920, 1080, 4, 14, 2, 20, 20, 2, 1);
            Log("БЕНЧ: " + pipe.Bench(60));
        }
        return 0;
    }

    static int SelfCheck()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
        using (dev)
        using (ctx)
        {
            using var pipe = new LivePipeline(dev, ctx, 320, 180, 4, 14, 2, 20, 20, 2, 1);
            Log("САМОПРОВЕРКА: " + pipe.SelfTest(4));
            Log("ПРОВЕРКА МАСКИ: " + pipe.OccStats());
        }
        return 0;
    }

    static Options ParseArgs(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string s = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new Exception("Нет значения для " + s);
            switch (s)
            {
                case "--gpu": o.Gpu = int.Parse(Next()); break;
                case "--output": o.Output = int.Parse(Next()); break;
                case "--mode": o.Mode = int.Parse(Next()); break;
                case "--median": o.Median = int.Parse(Next()); break;
                case "--subpixel": o.Subpix = int.Parse(Next()); break;
                case "--levels": o.Levels = int.Parse(Next()); break;
                case "--r0": o.R0 = int.Parse(Next()); break;
                case "--rr": o.Rr = int.Parse(Next()); break;
                case "--lambda": o.Lambda = (int)Math.Round(double.Parse(Next(), CultureInfo.InvariantCulture) * 1000); break;
                case "--thresh": o.Thresh = (int)Math.Round(double.Parse(Next(), CultureInfo.InvariantCulture) * 1000); break;
                case "--flip": o.Flip = true; break;
                case "--blt": o.Flip = false; break;
                case "--nodiag": o.Diag = false; break;
                case "--latency": o.Latency = int.Parse(Next()); break;
                case "--noprio": o.Prio = false; break;
                case "--lag": o.Lag = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--jit": o.JitGain = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--every": o.Every = int.Parse(Next()); break;
                case "--extrap": o.Extrap = Math.Clamp(double.Parse(Next().Replace(',', '.'), CultureInfo.InvariantCulture), 1.0, 1.8); break;
                case "--tsmooth": o.TSmooth = Math.Clamp(double.Parse(Next().Replace(',', '.'), CultureInfo.InvariantCulture), 0.0, 1.0); break;
                case "--mult": o.Mult = double.Parse(Next().Replace(',', '.'), CultureInfo.InvariantCulture); break;
                case "--hz": o.TargetHz = int.Parse(Next()); break;
                case "--selfcheck": o.SelfCheck = true; break;
                case "--bench": o.Bench = true; break;
                case "--nostatic": o.NoStatic = true; break;
                case "--noocc": o.NoOcc = true; break;
                case "--nohitch": o.HitchK = 0; break;
                case "--nofade": o.NoFade = true; break;
                case "--fade": o.NoFade = false; break;
                case "--tearing": o.Tearing = true; break;
                case "--vsync": o.Tearing = false; break;
                case "--noprev": o.Prev = false; break;
                case "--prev": o.Prev = true; break;
                case "--hitch": o.HitchK = double.Parse(Next().Replace(',', '.'), CultureInfo.InvariantCulture); break;
                case "--fast": o.Tune = -1; break;
                case "--smooth": o.Tune = 1; break;
                case "--delay": o.Delay = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                default: throw new Exception("Неизвестный параметр: " + s);
            }
        }
        if (o.Mode < 0 || o.Mode > 2) throw new Exception("--mode: 0, 1 или 2");
        if (o.Median < 0 || o.Median > 2) throw new Exception("--median: 0, 1 или 2");
        if (o.Subpix < 0 || o.Subpix > 1) throw new Exception("--subpixel: 0 или 1");
        if (o.Latency < 1 || o.Latency > 3) throw new Exception("--latency: 1, 2 или 3");
        if (o.Every < 0 || o.Every > 4) throw new Exception("--every: 1, 2, 3 или 4");
        return o;
    }

    static void RunLive(Options o, IDXGIAdapter1 adapter, IDXGIOutput output, IntPtr hwnd, int W, int H)
    {
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
        using var devOwner = dev;
        using var ctxOwner = ctx;

        using (var dxgiDev = dev.QueryInterface<IDXGIDevice1>())
        {
            // Для VSync (syncInterval = 1) требуется минимум 2 кадра очереди DXGI,
            // чтобы CPU и GPU работали параллельно и выдавали полные 140/144 Гц без просадки до 110-120.
            int lat = o.Tearing ? Math.Clamp(o.Latency, 1, 3) : Math.Clamp(Math.Max(2, o.Latency), 2, 3);
            dxgiDev.MaximumFrameLatency = (uint)lat;
        }

        string deviceName = output.Description.DeviceName;
        if (o.Prio)
        {
            try
            {
                int st = D3DKMTSetProcessSchedulingPriorityClass(Process.GetCurrentProcess().Handle, 4);
                Log(st == 0 ? "Приоритет GPU-планировщика: HIGH" : $"Приоритет GPU-планировщика не изменён (код {st:X})");
            }
            catch (Exception e) { Log("Приоритет GPU-планировщика: " + e.Message); }
        }

        int currentHz = o.TargetHz > 0 ? o.TargetHz : CurrentHz(deviceName);
        Log($"Старт v2: {W}x{H}, экран {currentHz} Гц{(o.TargetHz > 0 ? " (ручной ввод)" : "")}, очередь {o.Latency}, цепь {(o.Flip ? "flip" : "blt")}, extrap={o.Extrap:F2}, tsmooth={o.TSmooth:F2}");

        using var factory2 = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        var sd = new SwapChainDescription1
        {
            Width = (uint)W,
            Height = (uint)H,
            Format = Format.R8G8B8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = (uint)(o.Flip ? 3 : 1),
            Scaling = Scaling.Stretch,
            SwapEffect = o.Flip ? SwapEffect.FlipDiscard : SwapEffect.Discard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };
        IDXGISwapChain1 swap = null;
        if (o.Flip)
        {
            try
            {
                sd.Flags = SwapChainFlags.AllowTearing;
                swap = factory2.CreateSwapChainForHwnd(dev, hwnd, sd);
            }
            catch
            {
                sd.Flags = SwapChainFlags.None;
                swap = factory2.CreateSwapChainForHwnd(dev, hwnd, sd);
            }
        }
        else
        {
            swap = factory2.CreateSwapChainForHwnd(dev, hwnd, sd);
        }
        try { factory2.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll); } catch { }
        using var swapOwner = swap;
        using var backBuf = swap.GetBuffer<ID3D11Texture2D>(0);

        IDXGIOutputDuplication dup = null;
        bool MakeDup()
        {
            try
            {
                using var out1 = output.QueryInterface<IDXGIOutput1>();
                dup = out1.DuplicateOutput(dev);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine("Не удалось начать захват экрана: " + e.Message);
                return false;
            }
        }
        if (!MakeDup()) return;

        using var pipe = new LivePipeline(dev, ctx, W, H, o.Levels, o.R0, o.Rr, o.Lambda, o.Thresh, o.Median, o.Subpix);
        pipe.StaticSkip = !o.NoStatic;
        pipe.OccEnabled = !o.NoOcc;
        pipe.NoFade = o.NoFade;
        pipe.NoPrev = !o.Prev;

        long freq = Stopwatch.Frequency;
        double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / freq;
        double ToMs(long qpc) => qpc * 1000.0 / freq;

        double periodMs = 1000.0 / Math.Max(30, currentHz);
        double leadMs = 0.5 * periodMs;
        int presentSkippedCount = 0;
        int acqTry = 0, acqOk = 0, acqEmpty = 0, acqZeroTime = 0;
        string presentFailHex = "-";

        // Режим вывода: VSync (по умолчанию для максимальной плавности) либо Tearing (--tearing)
        bool tearingMode = o.Tearing;
        bool canTear = (sd.Flags & SwapChainFlags.AllowTearing) != 0;
        int presentSlowStreak = 0;

        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }
        catch { }

        bool haveB = false;
        double tA = 0, tB = 0, tLastSrc = 0, tSm = 0;
        var pendingTimestamps = new Queue<double>();
        bool haveS = false; double S = 0;
        double interval = 33.3;
        double prevInterval = 33.3;
        double delayMs = Math.Max(35.0, 1.5 * interval) + o.Lag;
        long ticksSinceUnder = 1000;
        double[] lastInts = new double[9];
        double[] sortBuf = new double[9];
        int intCount = 0, intHead = 0;
        bool formatChecked = false;

        long srcCount = 0, ddCount = 0, dupCount = 0, metaZero = 0, dropCount = 0;
        double srcFpsSeen = 0;
        double dtMin = 1e9, dtMax = 0;

        long outCount = 0, presentFail = 0, missedTicks = 0, underruns = 0, backtracks = 0, jumps = 0;
        double errMax = 0, pMin = 9, pMax = -9; long pLow = 0;
        double presentSum = 0, presentMax = 0;
        int[] hist = new int[5];
        double lastPostMs = NowMs();
        double srcPrev = double.NaN;
        bool useGrid = o.Flip;
        double vbPhase = lastPostMs;
        double periodMeas = periodMs;
        double nextDueMs = lastPostMs;
        double sIntegral = 0;
        double targetDelay = delayMs;

        long lastReport = Stopwatch.GetTimestamp();
        int prevMode = mode;
        string lastDiagText = "";

        bool pairHitch = false;
        long hitchPairs = 0;

        void CommitPair()
        {
            pipe.Commit();
            tA = tB;
            if (pendingTimestamps.Count > 0) tB = pendingTimestamps.Dequeue();

            double dur = tB - tA;
            pairHitch = o.HitchK > 0 && dur > o.HitchK * interval && dur > 1.0;
            if (pairHitch) hitchPairs++;
        }

        void SleepUntil(double dueMs)
        {
            while (true)
            {
                double left = dueMs - NowMs();
                if (left <= 0.03) return;
                if (left > 3.5) Thread.Sleep(1);
                else if (left > 0.05) Thread.SpinWait(30);
                else Thread.SpinWait(10);
            }
        }

        while (!quit)
        {
            int m = mode;
            double every;
            if (o.Mult > 0.01)
            {
                double srcFps = srcFpsSeen > 0.5 ? srcFpsSeen : 1000.0 / Math.Clamp(interval, 4.0, 120.0);
                double targetFps = srcFps * o.Mult;
                double tickHz = 1000.0 / periodMs;
                every = Math.Max(1.0, tickHz / Math.Max(1.0, targetFps));
            }
            else every = mult > 0 ? Math.Clamp(mult, 1, 8) : (o.Every > 0 ? o.Every : 1.0);
            if (m != prevMode) { pipe.InvalidateVectors(); prevMode = m; sIntegral = 0; }

            double now = NowMs();
            // Высокоточный таймерный метроном подачи кадров: строго держит частоту монитора без пропусков тактов
            if (nextDueMs <= 0 || Math.Abs(now - nextDueMs) > 3.0 * periodMs)
            {
                nextDueMs = now;
            }

            double sleepMs = (nextDueMs - 0.2) - now;
            if (sleepMs > 0.05)
            {
                SleepUntil(nextDueMs - 0.2);
                now = NowMs();
            }

            double T = nextDueMs + leadMs;
            nextDueMs += every * periodMs;

            // Защита от отставания таймера при длительной паузе или сворачивании (более 1.5 кадров)
            if (nextDueMs < now - 1.5 * periodMs)
            {
                nextDueMs = now;
            }

            // Плавное следование за целевым размером буфера (устраняет рывки)
            if (o.Delay <= 0 && m != 0)
            {
                double iv = Math.Clamp(interval, 8.0, 45.0);
                double baseK = o.Tune < 0 ? 1.35 : (o.Tune > 0 ? 2.15 : 1.85);
                targetDelay = baseK * iv;
                if (ticksSinceUnder < 120) targetDelay += (o.Tune < 0 ? 2.0 : 4.0);

                // Защита от истощения буфера (для быстрого пресета допускается меньший запас для минимизации инпутлага)
                double minFloor = iv * (o.Tune < 0 ? 1.15 : 1.50);
                if (delayMs < minFloor)
                    delayMs = Math.Max(delayMs, minFloor);

                double step = Math.Abs(targetDelay - delayMs) > 4.0 ? 0.40 : 0.10;
                delayMs += Math.Clamp(targetDelay - delayMs, -step, step);
            }
            else if (o.Delay > 0)
            {
                double minSafe = Math.Max(periodMs * 1.5, interval * 0.9);
                delayMs = Math.Max(o.Delay, minSafe);
            }

            double dtStep = periodMs * every;
            double target = T - delayMs;
            if (!haveS || Math.Abs(target - S) > 4.0 * interval)
            {
                S = target;
                sIntegral = 0;
                haveS = true;
            }
            else
            {
                // Фазовый регулятор темпа (PLL Pacer):
                // Исключает микро-рывки S, удерживая шаг строго вокруг dtStep
                double sErr = target - (S + dtStep);
                sIntegral = Math.Clamp(sIntegral + sErr * 0.002, -2.0, 2.0);
                double corr = 0.08 * sErr + sIntegral;
                double maxSlew = Math.Abs(sErr) > 3.0 ? 0.25 * dtStep : 0.08 * dtStep;
                corr = Math.Clamp(corr, -maxSlew, maxSlew);
                S += dtStep + corr;
            }

            // 1. Захват новых кадров игры
            bool newFrame = false, dropped = false;
            for (int k = 0; k < 4; k++)
            {
                var r = dup.AcquireNextFrame(0, out OutduplFrameInfo info, out IDXGIResource res);
                acqTry++;
                if (!r.Success)
                {
                    acqEmpty++;
                    if (r == Vortice.DXGI.ResultCode.AccessLost || r == Vortice.DXGI.ResultCode.InvalidCall)
                    {
                        dup?.Dispose(); dup = null;
                        Thread.Sleep(300);
                        if (!MakeDup()) return;
                        haveB = false; tLastSrc = 0;
                        continue;
                    }
                    break;
                }
                try
                {
                    if (info.LastPresentTime == 0) { acqZeroTime++; continue; }
                    using var tex = res.QueryInterface<ID3D11Texture2D>();
                    if (!formatChecked)
                    {
                        var d = tex.Description;
                        if (d.Format != Format.B8G8R8A8_UNorm || (int)d.Width != W || (int)d.Height != H)
                        {
                            Console.WriteLine($"Неподдерживаемый формат захвата: {d.Format} {d.Width}x{d.Height} (нужен BGRA {W}x{H}; выключите HDR).");
                            return;
                        }
                        formatChecked = true;
                    }
                    ddCount++; acqOk++;
                    if (info.TotalMetadataBufferSize == 0) metaZero++;
                    if (!pipe.Probe(tex, info.TotalMetadataBufferSize > 0 || info.AccumulatedFrames > 1)) { dupCount++; continue; }

                    double tArr = NowMs();
                    double tSrcRaw = ToMs((long)info.LastPresentTime);
                    if (tSrcRaw <= 0 || tSrcRaw > tArr + 2.0 || tSrcRaw < tArr - 400.0) tSrcRaw = tArr;

                    // TSmooth и адаптивное микро-сглаживание шума DWM захвата
                    double tSrc = tSrcRaw;
                    if (o.TSmooth > 0.001)
                    {
                        if (tSm <= 0 || Math.Abs(tSrcRaw - (tSm + interval)) > 8.0)
                        {
                            tSm = tSrcRaw;
                            tSrc = tSrcRaw;
                        }
                        else
                        {
                            double expect = tSm + interval;
                            double devMs = tSrcRaw - expect;
                            tSm = expect + devMs * Math.Clamp(o.TSmooth, 0.05, 0.5);
                            tSrc = tSm;
                        }
                    }
                    else if (tLastSrc > 0)
                    {
                        // Адаптивное микро-сглаживание дрожания DWM композитора (до 2.5 мс)
                        double expected = tLastSrc + interval;
                        double diffJitter = tSrcRaw - expected;
                        if (Math.Abs(diffJitter) < 2.5)
                        {
                            tSrc = expected + diffJitter * 0.35;
                        }
                    }

                    if (tLastSrc > 0)
                    {
                        double dt = tSrc - tLastSrc;
                        if (dt > 1.0 && dt < 250.0)
                        {
                            if (dt < dtMin) dtMin = dt;
                            if (dt > dtMax) dtMax = dt;
                            lastInts[intHead] = dt;
                            intHead = (intHead + 1) % 9;
                            if (intCount < 9) intCount++;
                            Array.Copy(lastInts, sortBuf, intCount);
                            Array.Sort(sortBuf, 0, intCount);
                            interval = Math.Max(4.0, sortBuf[intCount / 2]);
                        }
                    }
                    tLastSrc = tSrc;
                    srcCount++;
                    newFrame = true;

                    if (!haveB)
                    {
                        pipe.Accept(m == 1);
                        tA = tB = tSrc;
                        haveB = true;
                        continue;
                    }

                    pipe.Accept(m == 1);
                    pendingTimestamps.Enqueue(tSrc);
                    if (m == 0) CommitPair();
                }
                finally
                {
                    res.Dispose();
                    dup.ReleaseFrame();
                }
            }

            // 2. Расчет позиции между кадрами (p)
            double p = 1.0;
            bool underrun = false;

            if (haveB && m != 0)
            {
                while (pipe.HasPending && S >= tB)
                {
                    CommitPair();
                }

                double denom = Math.Max(1.0, tB - tA);
                p = (S - tA) / denom;

                if (p > 1.0 && pipe.HasPending)
                {
                    CommitPair();
                    denom = Math.Max(1.0, tB - tA);
                    p = (S - tA) / denom;
                }

                if (p > 1.0)
                {
                    p = Math.Min(p, o.Extrap); // Экстраполяция движения вперёд
                    underrun = true;
                }
                if (p < 0.0) p = 0.0;
                if (p < pMin) pMin = p;
                if (p > pMax) pMax = p;
                if (p < 0.03) pLow++;
            }

            if (underrun) { underruns++; ticksSinceUnder = 0; }
            else if (dropped) ticksSinceUnder = 0;
            else ticksSinceUnder++;

            // 3. Построение кадра и показ
            if (!newFrame) pipe.FinishSearch();
            pipe.Render((int)Math.Round(p * 1000.0), m == 2 || pairHitch, backBuf);

            double tPre = NowMs();
            uint syncInterval = (uint)(tearingMode ? 0 : 1);
            PresentFlags presFlags = (tearingMode && canTear)
                ? PresentFlags.AllowTearing
                : PresentFlags.None;
            var presRes = swap.Present(syncInterval, presFlags);
            if (presRes.Failure && tearingMode)
            {
                presRes = swap.Present(0, PresentFlags.None);
            }
            bool presentSkipped = presRes.Code == unchecked((int)0x087A0001)   // DXGI_STATUS_WAS_STILL_DRAWING
                               || presRes.Code == unchecked((int)0x887A000A);  // DXGI_ERROR_WAS_STILL_DRAWING
            double tPost = NowMs();

            double prevPostMs = lastPostMs;
            double dp = tPost - prevPostMs;

            if (presentSkipped)
            {
                presentSkippedCount++;
                Thread.Sleep(1);
            }
            else
            {
                if (presRes.Failure) { presentFail++; presentFailHex = presRes.Code.ToString("X8"); }
                double presentMs = tPost - tPre;
                presentSum += presentMs;
                if (presentMs > presentMax) presentMax = presentMs;
                hist[presentMs < 9 ? 0 : presentMs < 14 ? 1 : presentMs < 20 ? 2 : presentMs < 30 ? 3 : 4]++;
                outCount++;

                if (dp > 1.55 * every * periodMs) missedTicks++;
            }

            if (useGrid && !tearingMode)
            {
                double d = tPost - vbPhase;
                if (Math.Abs(d) < 0.6 * every * periodMeas) vbPhase += 0.10 * d;
                else vbPhase = tPost;

                if (dp > 0.6 * every * periodMeas && dp < 1.4 * every * periodMeas)
                    periodMeas += 0.02 * (dp / every - periodMeas);
                periodMeas = Math.Max(0.85 * periodMs, Math.Min(1.15 * periodMs, periodMeas));
            }

            double srcShown = haveB ? tA + p * Math.Max(1.0, tB - tA) : 0;
            if (haveB && !double.IsNaN(srcPrev))
            {
                double err = (srcShown - srcPrev) - dp;
                if (Math.Abs(err) > Math.Abs(errMax)) errMax = err;
                if (err < -2.0) backtracks++;
                else if (err > 2.0) jumps++;
            }
            srcPrev = haveB ? srcShown : double.NaN;
            lastPostMs = tPost;

            // 4. Отчет и адаптация раз в секунду
            long nowTs = Stopwatch.GetTimestamp();
            if ((nowTs - lastReport) >= freq)
            {
                double secs = (nowTs - lastReport) / (double)freq;

                // Проверка изменения герцовки экрана (только при авто-режиме)
                if (o.TargetHz <= 0)
                {
                    int newHz = CurrentHz(deviceName);
                    if (newHz > 0 && newHz != currentHz)
                    {
                        Log($">>> Частота экрана изменилась: {currentHz} FPS -> {newHz} FPS (пересчёт такта)");
                        currentHz = newHz;
                        periodMs = 1000.0 / Math.Max(30, currentHz);
                        leadMs = 0.5 * periodMs;
                        periodMeas = periodMs;
                        vbPhase = now;
                    }
                }

                prevInterval = interval;

                string diagText = "";
                if (o.Diag && haveB && m != 0 && !pipe.HasPending)
                {
                    var dg = pipe.Diag();
                    if (dg.est >= 0) lastDiagText = $" | GPU: поиск {F(dg.est)} мс, кадр {F(dg.render)} мс";
                    diagText = lastDiagText;
                }
                srcFpsSeen = srcCount / secs;
                double curPresentAvg = outCount > 0 ? presentSum / outCount : 0;
                string tearingLabel = tearingMode ? " [tearing]" : "";
                Log($"НОВЫХ кадров игры {F(srcCount / secs)}/с (захват {F(ddCount / secs)}/с, повторов {dupCount}, выброшено {dropCount}) | вывод {F(outCount / secs)}/с | интервал {F(interval)} мс (мин {F(dtMin >= 1e9 ? 0: dtMin)}, макс {F(dtMax)}) | режим {m} | запас {F(delayMs)} мс | Present ср {F(curPresentAvg)}, макс {F(presentMax)} мс | <9/<14/<20/<30/>=30: {hist[0]}/{hist[1]}/{hist[2]}/{hist[3]}/{hist[4]} | сбоев {presentFail} ({presentFailHex}), пропусков {presentSkippedCount} | приём: попыток {acqTry}, кадров {acqOk}, пусто {acqEmpty}, без времени {acqZeroTime} | Windows {currentHz} FPS{diagText}{tearingLabel}");
                Log($"   ПЛАВНОСТЬ: пропущено тактов {missedTicks}, нехваток кадра {underruns}, замираний {backtracks}, скачков {jumps}, худшее отклонение {F(errMax, "F2")} мс | p {F(pMin, "F2")}..{F(pMax, "F2")} у края {pLow} | рывков подачи {hitchPairs} | такт {F(periodMs, "F2")} мс | показ {(every <= 1.0001 ? "каждый такт" : "шаг " + F(every, "F2") + " такта")}, множитель {(o.Mult > 0 ? "x" + F(o.Mult, "F1") : (o.TargetHz > 0 ? o.TargetHz + " FPS" : "авто"))}");

                // Авто-детект тормозного VSync Present: если Present в среднем дольше такта,
                // переключаемся на tearing+таймер, полностью обходя DWM VSync блокировку.
                if (!tearingMode && canTear)
                {
                    if (curPresentAvg > periodMs * 1.5 && missedTicks > 35)
                    {
                        presentSlowStreak++;
                        if (presentSlowStreak >= 3)
                        {
                            tearingMode = true;
                            nextDueMs = NowMs();
                            Log($">>> VSync Present заблокирован драйвером ({F(curPresentAvg)} мс > такт {F(periodMs)} мс): переход на tearing + таймер");
                        }
                    }
                    else presentSlowStreak = 0;
                }
                presentSum = 0; presentMax = 0; ddCount = 0; dupCount = 0; metaZero = 0; dropCount = 0;
                Array.Clear(hist, 0, hist.Length); dtMin = 1e9; dtMax = 0; presentFail = 0; presentSkippedCount = 0; acqTry = 0; acqOk = 0; acqEmpty = 0; acqZeroTime = 0;
                srcCount = 0; outCount = 0; missedTicks = 0; hitchPairs = 0; underruns = 0; backtracks = 0; jumps = 0; errMax = 0; pMin = 9; pMax = -9; pLow = 0;
                lastReport = Stopwatch.GetTimestamp();
            }
        }

        dup?.Dispose();
    }

    sealed class OverlayForm : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, LWA_ALPHA = 2, WDA_EXCLUDEFROMCAPTURE = 0x11;

        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public Action OnToggle, OnQuit;
        public Action<int> OnMult;

        public OverlayForm(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
            if (!SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE))
                Console.WriteLine("ВНИМАНИЕ: не удалось исключить окно из захвата (нужна Windows 10 версии 2004+).");
            if (!RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_ALT, 0x77)) Console.WriteLine("Не удалось занять Ctrl+Alt+F8");
            if (!RegisterHotKey(Handle, 2, MOD_CONTROL | MOD_ALT, 0x79)) Console.WriteLine("Не удалось занять Ctrl+Alt+F10");
            for (int d = 0; d <= 9; d++) RegisterHotKey(Handle, MultHotId(d), MOD_CONTROL | MOD_ALT, (uint)(0x30 + d));
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterHotKey(Handle, 1);
            UnregisterHotKey(Handle, 2);
            for (int d = 0; d <= 9; d++) UnregisterHotKey(Handle, MultHotId(d));
            base.OnHandleDestroyed(e);
        }

        static int MultHotId(int d) => d == 0 ? 12 : 2 + d;

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTTRANSPARENT = -1;
            const int WM_SETCURSOR = 0x0020;
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
            if (m.Msg == WM_SETCURSOR)
            {
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_HOTKEY)
            {
                if (m.WParam.ToInt32() == 1) OnToggle?.Invoke();
                else if (m.WParam.ToInt32() == 2) OnQuit?.Invoke();
                else if (m.WParam.ToInt32() == 12) OnMult?.Invoke(0);
                else if (m.WParam.ToInt32() >= 3 && m.WParam.ToInt32() <= 11) OnMult?.Invoke(m.WParam.ToInt32() - 2);
                return;
            }
            base.WndProc(ref m);
        }
    }

    sealed class LivePipeline : IDisposable
    {
        readonly ID3D11Device dev;
        readonly ID3D11DeviceContext ctx;
        readonly int W, H, L, R0, Rr, Lam, Thresh, MedianMode, Subpix;
        public bool StaticSkip = true;
        public bool OccOn = true;
        public bool OccEnabled = true;
        public bool NoFade = false;
        public bool NoPrev = false;
        readonly int[] lw, lh, bw, bh;
        readonly List<IDisposable> owned = new();

        public const int MAX_PENDING = 4;
        readonly ID3D11ComputeShader csLuma, csDown, csSearch, csSubpix, csMedian, csInterp, csDiff, csStatic;
        readonly ID3D11Buffer cb;
        readonly ID3D11SamplerState smp;
        readonly ID3D11Texture2D texA, texB, texCand, outTex, vecSub, vecShown, diffTex, stDiff;
        readonly ID3D11Texture2D[] texPending = new ID3D11Texture2D[MAX_PENDING];
        readonly ID3D11ShaderResourceView srvA, srvB, srvCand, srvVecSub, srvVecShown;
        readonly ID3D11ShaderResourceView[] srvPending = new ID3D11ShaderResourceView[MAX_PENDING];
        readonly ID3D11UnorderedAccessView uavOut, uavVecSub, uavDiff;
        readonly ID3D11Texture2D texStatic, texPrev;
        readonly ID3D11Texture2D[] vecB;
        readonly ID3D11Texture2D[] vecTmpB;
        readonly ID3D11UnorderedAccessView[] uavVecB, uavVecTmpB;
        readonly ID3D11ShaderResourceView[] srvVecB, srvVecTmpB, srvFinalB;
        readonly ID3D11ShaderResourceView srvPrev;
        ID3D11ShaderResourceView srvFinal1;
        readonly ID3D11ShaderResourceView srvStatic;
        readonly ID3D11UnorderedAccessView uavStatic;
        readonly ID3D11Texture2D[] lumaA, lumaB, vec, vecTmp;
        readonly ID3D11ShaderResourceView[] srvLA, srvLB, srvVec, srvVecTmp, srvFinal;
        readonly ID3D11UnorderedAccessView[] uavLA, uavLB, uavVec, uavVecTmp;

        readonly ID3D11Query qDisjoint, qT0, qTMid, qT1;

        public int FrameCount { get; private set; }
        bool vecValid;
        bool searchPending;
        bool vecReady;
        int pendingHead = 0;
        int pendingCount = 0;
        bool wantSearch;
        public bool HasPending => pendingCount > 0;
        public int PendingCount => pendingCount;

        T Own<T>(T x) where T : IDisposable { owned.Add(x); return x; }

        ID3D11Texture2D Tex(Format f, int w, int h, BindFlags bf)
            => Own(dev.CreateTexture2D(new Texture2DDescription(f, (uint)w, (uint)h, 1, 1, bf, ResourceUsage.Default, CpuAccessFlags.None)));

        static ID3D11ComputeShader Compile(ID3D11Device dev, string entry)
        {
            var hr = Compiler.Compile(Shaders.Source, entry, "shaders.hlsl", "cs_5_0", out Blob blob, out Blob err);
            if (hr.Failure)
                throw new Exception($"Ошибка компиляции шейдера {entry}: " + (err != null ? err.AsString() : hr.ToString()));
            var cs = dev.CreateComputeShader(blob.AsBytes());
            blob.Dispose(); err?.Dispose();
            return cs;
        }

        public LivePipeline(ID3D11Device dev, ID3D11DeviceContext ctx, int w, int h, int levels, int r0, int rr, int lam, int thresh, int medianMode, int subpix)
        {
            this.dev = dev; this.ctx = ctx;
            W = w; H = h; R0 = r0; Rr = rr; Lam = lam; Thresh = thresh; MedianMode = medianMode; Subpix = subpix;
            L = Math.Max(1, levels);
            while (L > 1 && ((w >> (L - 1)) < 8 || (h >> (L - 1)) < 8)) L--;

            lw = new int[L]; lh = new int[L]; bw = new int[L]; bh = new int[L];
            lw[0] = w; lh[0] = h;
            for (int l = 1; l < L; l++) { lw[l] = (lw[l - 1] + 1) / 2; lh[l] = (lh[l - 1] + 1) / 2; }
            for (int l = 0; l < L; l++) { bw[l] = (lw[l] + 7) / 8; bh[l] = (lh[l] + 7) / 8; }

            csLuma = Own(Compile(dev, "LumaCS"));
            csDown = Own(Compile(dev, "DownCS"));
            csSearch = Own(Compile(dev, "SearchCS"));
            csSubpix = Own(Compile(dev, "SubpixCS"));
            csMedian = Own(Compile(dev, "MedianCS"));
            csInterp = Own(Compile(dev, "InterpCS"));
            csDiff = Own(Compile(dev, "DiffCS"));
            csStatic = Own(Compile(dev, "StaticCS"));

            cb = Own(dev.CreateBuffer(new BufferDescription(64, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write)));
            smp = Own(dev.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue
            }));

            texA = Tex(Format.B8G8R8A8_UNorm, w, h, BindFlags.ShaderResource);
            texB = Tex(Format.B8G8R8A8_UNorm, w, h, BindFlags.ShaderResource);
            texCand = Tex(Format.B8G8R8A8_UNorm, w, h, BindFlags.ShaderResource);
            for (int i = 0; i < MAX_PENDING; i++)
            {
                texPending[i] = Tex(Format.B8G8R8A8_UNorm, w, h, BindFlags.ShaderResource);
                srvPending[i] = Own(dev.CreateShaderResourceView(texPending[i]));
            }
            srvCand = Own(dev.CreateShaderResourceView(texCand));
            diffTex = Tex(Format.R32_UInt, 1, 1, BindFlags.UnorderedAccess);
            uavDiff = Own(dev.CreateUnorderedAccessView(diffTex));
            stDiff = Own(dev.CreateTexture2D(new Texture2DDescription(Format.R32_UInt, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)));
            srvA = Own(dev.CreateShaderResourceView(texA));
            srvB = Own(dev.CreateShaderResourceView(texB));

            outTex = Tex(Format.R8G8B8A8_UNorm, w, h, BindFlags.UnorderedAccess);
            uavOut = Own(dev.CreateUnorderedAccessView(outTex));

            vecSub = Tex(Format.R32_SInt, bw[0], bh[0], BindFlags.ShaderResource | BindFlags.UnorderedAccess);
            srvVecSub = Own(dev.CreateShaderResourceView(vecSub));
            uavVecSub = Own(dev.CreateUnorderedAccessView(vecSub));
            texStatic = Tex(Format.R32_UInt, bw[0], bh[0], BindFlags.ShaderResource | BindFlags.UnorderedAccess);
            srvStatic = Own(dev.CreateShaderResourceView(texStatic));
            vecB = new ID3D11Texture2D[L]; vecTmpB = new ID3D11Texture2D[L];
            uavVecB = new ID3D11UnorderedAccessView[L]; uavVecTmpB = new ID3D11UnorderedAccessView[L];
            srvVecB = new ID3D11ShaderResourceView[L]; srvVecTmpB = new ID3D11ShaderResourceView[L];
            srvFinalB = new ID3D11ShaderResourceView[L];
            for (int l = 1; l < L; l++)
            {
                vecB[l] = Tex(Format.R32_SInt, bw[l], bh[l], BindFlags.ShaderResource | BindFlags.UnorderedAccess);
                vecTmpB[l] = Tex(Format.R32_SInt, bw[l], bh[l], BindFlags.ShaderResource | BindFlags.UnorderedAccess);
                uavVecB[l] = Own(dev.CreateUnorderedAccessView(vecB[l]));
                uavVecTmpB[l] = Own(dev.CreateUnorderedAccessView(vecTmpB[l]));
                srvVecB[l] = Own(dev.CreateShaderResourceView(vecB[l]));
                srvVecTmpB[l] = Own(dev.CreateShaderResourceView(vecTmpB[l]));
            }
            texPrev = Tex(Format.R32_SInt, bw[0], bh[0], BindFlags.ShaderResource);
            srvPrev = Own(dev.CreateShaderResourceView(texPrev));
            uavStatic = Own(dev.CreateUnorderedAccessView(texStatic));
            vecShown = Tex(Format.R32_SInt, bw[0], bh[0], BindFlags.ShaderResource);
            srvVecShown = Own(dev.CreateShaderResourceView(vecShown));

            lumaA = new ID3D11Texture2D[L]; lumaB = new ID3D11Texture2D[L]; vec = new ID3D11Texture2D[L]; vecTmp = new ID3D11Texture2D[L];
            srvLA = new ID3D11ShaderResourceView[L]; srvLB = new ID3D11ShaderResourceView[L];
            srvVec = new ID3D11ShaderResourceView[L]; srvVecTmp = new ID3D11ShaderResourceView[L]; srvFinal = new ID3D11ShaderResourceView[L];
            uavLA = new ID3D11UnorderedAccessView[L]; uavLB = new ID3D11UnorderedAccessView[L];
            uavVec = new ID3D11UnorderedAccessView[L]; uavVecTmp = new ID3D11UnorderedAccessView[L];
            for (int l = 0; l < L; l++)
            {
                var bf = BindFlags.ShaderResource | BindFlags.UnorderedAccess;
                lumaA[l] = Tex(Format.R8_UNorm, lw[l], lh[l], bf);
                lumaB[l] = Tex(Format.R8_UNorm, lw[l], lh[l], bf);
                vec[l] = Tex(Format.R32_SInt, bw[l], bh[l], bf);
                vecTmp[l] = Tex(Format.R32_SInt, bw[l], bh[l], bf);
                srvLA[l] = Own(dev.CreateShaderResourceView(lumaA[l]));
                srvLB[l] = Own(dev.CreateShaderResourceView(lumaB[l]));
                srvVec[l] = Own(dev.CreateShaderResourceView(vec[l]));
                srvVecTmp[l] = Own(dev.CreateShaderResourceView(vecTmp[l]));
                uavLA[l] = Own(dev.CreateUnorderedAccessView(lumaA[l]));
                uavLB[l] = Own(dev.CreateUnorderedAccessView(lumaB[l]));
                uavVec[l] = Own(dev.CreateUnorderedAccessView(vec[l]));
                uavVecTmp[l] = Own(dev.CreateUnorderedAccessView(vecTmp[l]));
                srvFinal[l] = srvVec[l];
            }

            qDisjoint = Own(dev.CreateQuery(QueryType.TimestampDisjoint));
            qT0 = Own(dev.CreateQuery(QueryType.Timestamp));
            qTMid = Own(dev.CreateQuery(QueryType.Timestamp));
            qT1 = Own(dev.CreateQuery(QueryType.Timestamp));

            ctx.CSSetConstantBuffer(0, cb);
            ctx.CSSetSampler(0, smp);
        }

        public void Dispose() { foreach (var o in owned) o.Dispose(); owned.Clear(); }
        public void InvalidateVectors() { vecValid = false; }
        static bool Done(object r) => r is bool b ? b : (r is Result res && res.Code == 0);

        public (double est, double render) Diag()
        {
            if (pendingCount > 0) return (-1, -1);
            ctx.Begin(qDisjoint);
            ctx.End(qT0);
            RecordStage1(srvA, srvB); RecordStage2();
            ctx.End(qTMid);
            SetCb(W, H, bw[0], bh[0], 0, 0, 0, 0, 0, Thresh, 500, 0);
            Pass5(csInterp, srvA, srvB, srvVecShown, srvVecB[Math.Min(1, L - 1)], srvFinal1, uavOut, G(W), G(H));
            ctx.End(qT1);
            ctx.End(qDisjoint);
            ctx.Flush();

            QueryDataTimestampDisjoint dj = default;
            object r;
            do { r = ctx.GetData(qDisjoint, out dj); } while (!Done(r));
            ulong t0 = 0, tm = 0, t1 = 0;
            do { r = ctx.GetData(qT0, out t0); } while (!Done(r));
            do { r = ctx.GetData(qTMid, out tm); } while (!Done(r));
            do { r = ctx.GetData(qT1, out t1); } while (!Done(r));

            double f = 1000.0 / dj.Frequency;
            return ((tm - t0) * f, (t1 - tm) * f);
        }

        public uint LastDiff;

        public bool Probe(ID3D11Texture2D captured, bool hasMetadata = true)
        {
            ctx.CopyResource(texCand, captured);
            if (FrameCount == 0) return true;
            if (hasMetadata)
            {
                LastDiff = 10000;
                return true;
            }
            SetCb(W, H, 0, 0, 0, 0, 0, 0, 0, 0);
            Pass2(csDiff, srvCand, pendingCount > 0 ? srvPending[pendingHead] : srvB, uavDiff, 1, 1);
            ctx.CopyResource(stDiff, diffTex);
            var m = ctx.Map(stDiff, 0, MapMode.Read, MapFlags.None);
            uint v = (uint)Marshal.ReadInt32(m.DataPointer);
            ctx.Unmap(stDiff, 0);
            LastDiff = v;
            return v > 0;
        }

        public void Accept(bool search, bool fromA = false)
        {
            if (FrameCount == 0)
            {
                ctx.CopyResource(texB, texCand);
                ctx.CopyResource(texA, texB);
                FrameCount = 1;
                vecValid = false;
                wantSearch = search;
                return;
            }

            if (pendingCount < MAX_PENDING)
            {
                int tail = (pendingHead + pendingCount) % MAX_PENDING;
                ctx.CopyResource(texPending[tail], texCand);
                pendingCount++;
                wantSearch = search;

                if (pendingCount == 1)
                {
                    vecReady = false;
                    searchPending = false;
                    if (search)
                    {
                        RecordStage1(fromA ? srvA : srvB, srvPending[pendingHead]);
                        searchPending = true;
                    }
                }
            }
            else
            {
                int tail = (pendingHead + pendingCount - 1) % MAX_PENDING;
                ctx.CopyResource(texPending[tail], texCand);
            }
        }

        public void FinishSearch()
        {
            if (!searchPending) return;
            RecordStage2();
            searchPending = false;
            vecReady = true;
        }

        public bool Commit()
        {
            if (pendingCount == 0) return false;
            FinishSearch();
            ctx.CopyResource(texA, texB);
            ctx.CopyResource(texB, texPending[pendingHead]);
            if (vecReady) { ctx.CopyResource(vecShown, vecSub); vecValid = true; }
            else vecValid = false;
            vecReady = false;

            pendingHead = (pendingHead + 1) % MAX_PENDING;
            pendingCount--;

            if (pendingCount > 0)
            {
                searchPending = false;
                if (wantSearch)
                {
                    RecordStage1(srvB, srvPending[pendingHead]);
                    searchPending = true;
                }
            }
            FrameCount++;
            return pendingCount > 0;
        }

        public void Render(int tMilli, bool blend, ID3D11Texture2D backBuffer)
        {
            bool useBlend = blend && FrameCount >= 2;
            if (!vecValid && !useBlend) tMilli = 1000;
            SetCb(W, H, bw[0], bh[0], 0, 0, 0, 0, 0, Thresh, tMilli, useBlend ? 1 : (NoFade ? 2 : 0), 0, 0, OccOn ? 1 : 0);
            Pass5(csInterp, srvA, srvB, srvVecShown, srvVecB[Math.Min(1, L - 1)], srvFinal1, uavOut, G(W), G(H));
            if (backBuffer != null) ctx.CopyResource(backBuffer, outTex);
        }

        readonly int[] cbData = new int[16];
        void SetCb(int w, int h, int bw_, int bh_, int pbw, int pbh, int r, int lam, int useParent, int thresh, int tMilli = 500, int blendMode = 0, int useStatic = 0, int usePrev = 0, int useOcc = 0)
        {
            cbData[0] = w; cbData[1] = h; cbData[2] = bw_; cbData[3] = bh_;
            cbData[4] = pbw; cbData[5] = pbh; cbData[6] = r; cbData[7] = lam;
            cbData[8] = useParent; cbData[9] = thresh; cbData[10] = tMilli; cbData[11] = useOcc;
            cbData[12] = 1400; cbData[13] = blendMode; cbData[14] = useStatic; cbData[15] = usePrev;
            var m = ctx.Map(cb, 0, MapMode.WriteDiscard, MapFlags.None);
            Marshal.Copy(cbData, 0, m.DataPointer, 16);
            ctx.Unmap(cb, 0);
        }

        void Pass1(ID3D11ComputeShader cs, ID3D11ShaderResourceView s0, ID3D11UnorderedAccessView u0, int gx, int gy)
        {
            ctx.CSSetShader(cs);
            ctx.CSSetShaderResource(0, s0);
            ctx.CSSetUnorderedAccessView(0, u0);
            ctx.Dispatch((uint)gx, (uint)gy, 1);
            ctx.CSSetUnorderedAccessView(0, null);
            ctx.CSSetShaderResource(0, null);
        }

        void Pass2(ID3D11ComputeShader cs, ID3D11ShaderResourceView s0, ID3D11ShaderResourceView s1, ID3D11UnorderedAccessView u0, int gx, int gy)
        {
            ctx.CSSetShader(cs);
            ctx.CSSetShaderResource(0, s0);
            ctx.CSSetShaderResource(1, s1);
            ctx.CSSetUnorderedAccessView(0, u0);
            ctx.Dispatch((uint)gx, (uint)gy, 1);
            ctx.CSSetUnorderedAccessView(0, null);
            ctx.CSSetShaderResource(0, null);
            ctx.CSSetShaderResource(1, null);
        }

        void Pass3(ID3D11ComputeShader cs, ID3D11ShaderResourceView s0, ID3D11ShaderResourceView s1, ID3D11ShaderResourceView s2, ID3D11UnorderedAccessView u0, int gx, int gy)
        {
            ctx.CSSetShader(cs);
            ctx.CSSetShaderResource(0, s0);
            ctx.CSSetShaderResource(1, s1);
            ctx.CSSetShaderResource(2, s2);
            ctx.CSSetUnorderedAccessView(0, u0);
            ctx.Dispatch((uint)gx, (uint)gy, 1);
            ctx.CSSetUnorderedAccessView(0, null);
            ctx.CSSetShaderResource(0, null);
            ctx.CSSetShaderResource(1, null);
            ctx.CSSetShaderResource(2, null);
        }

        void Pass4(ID3D11ComputeShader cs, ID3D11ShaderResourceView s0, ID3D11ShaderResourceView s1, ID3D11ShaderResourceView s2, ID3D11ShaderResourceView s3, ID3D11UnorderedAccessView u0, int gx, int gy)
        {
            ctx.CSSetShader(cs);
            ctx.CSSetShaderResource(0, s0);
            ctx.CSSetShaderResource(1, s1);
            ctx.CSSetShaderResource(2, s2);
            ctx.CSSetShaderResource(3, s3);
            ctx.CSSetUnorderedAccessView(0, u0);
            ctx.Dispatch((uint)gx, (uint)gy, 1);
            ctx.CSSetUnorderedAccessView(0, null);
            ctx.CSSetShaderResource(0, null);
            ctx.CSSetShaderResource(1, null);
            ctx.CSSetShaderResource(2, null);
            ctx.CSSetShaderResource(3, null);
        }

        void Pass5(ID3D11ComputeShader cs, ID3D11ShaderResourceView s0, ID3D11ShaderResourceView s1, ID3D11ShaderResourceView s2, ID3D11ShaderResourceView s3, ID3D11ShaderResourceView s4, ID3D11UnorderedAccessView u0, int gx, int gy)
        {
            ctx.CSSetShader(cs);
            ctx.CSSetShaderResource(0, s0);
            ctx.CSSetShaderResource(1, s1);
            ctx.CSSetShaderResource(2, s2);
            ctx.CSSetShaderResource(3, s3);
            ctx.CSSetShaderResource(4, s4);
            ctx.CSSetUnorderedAccessView(0, u0);
            ctx.Dispatch((uint)gx, (uint)gy, 1);
            ctx.CSSetUnorderedAccessView(0, null);
            ctx.CSSetShaderResource(0, null);
            ctx.CSSetShaderResource(1, null);
            ctx.CSSetShaderResource(2, null);
            ctx.CSSetShaderResource(3, null);
            ctx.CSSetShaderResource(4, null);
        }

        void Pass(ID3D11ComputeShader cs, ID3D11ShaderResourceView[] srvs, ID3D11UnorderedAccessView[] uavs, int gx, int gy)
        {
            ctx.CSSetShader(cs);
            for (int i = 0; i < srvs.Length; i++) ctx.CSSetShaderResource((uint)i, srvs[i]);
            for (int i = 0; i < uavs.Length; i++) ctx.CSSetUnorderedAccessView((uint)i, uavs[i]);
            ctx.Dispatch((uint)gx, (uint)gy, 1);
            for (int i = 0; i < uavs.Length; i++) ctx.CSSetUnorderedAccessView((uint)i, null);
            for (int i = 0; i < srvs.Length; i++) ctx.CSSetShaderResource((uint)i, null);
        }

        static int G(int n) => (n + 15) / 16;

        void MaybeMedian(int l)
        {
            bool apply = MedianMode == 2 || (MedianMode == 1 && l == 0);
            if (!apply) return;
            SetCb(lw[l], lh[l], bw[l], bh[l], 0, 0, 0, 0, 0, 0);
            Pass1(csMedian, srvVec[l], uavVecTmp[l], G(bw[l]), G(bh[l]));
            srvFinal[l] = srvVecTmp[l];
        }

        void RecordStage1(ID3D11ShaderResourceView sA, ID3D11ShaderResourceView sB)
        {
            for (int l = 0; l < L; l++) srvFinal[l] = srvVec[l];
            for (int l = 1; l < L; l++) srvFinalB[l] = srvVecB[l];
            srvFinal1 = null;

            SetCb(W, H, 0, 0, 0, 0, 0, 0, 0, 0);
            Pass1(csLuma, sA, uavLA[0], G(W), G(H));
            SetCb(W, H, 0, 0, 0, 0, 0, 0, 0, 0);
            Pass1(csLuma, sB, uavLB[0], G(W), G(H));
            for (int l = 1; l < L; l++)
            {
                SetCb(lw[l], lh[l], 0, 0, 0, 0, 0, 0, 0, 0);
                Pass1(csDown, srvLA[l - 1], uavLA[l], G(lw[l]), G(lh[l]));
                SetCb(lw[l], lh[l], 0, 0, 0, 0, 0, 0, 0, 0);
                Pass1(csDown, srvLB[l - 1], uavLB[l], G(lw[l]), G(lh[l]));
            }
            int top = L - 1;
            SetCb(lw[top], lh[top], bw[top], bh[top], 1, 1, R0, Lam, 0, 0);
            Pass2(csSearch, srvLA[top], srvLB[top], uavVec[top], bw[top], bh[top]);
            MaybeMedian(top);
        }

        void RecordStage2()
        {
            for (int l = L - 2; l >= 0; l--)
            {
                if (l == 0 && StaticSkip)
                {
                    SetCb(W, H, bw[0], bh[0], 0, 0, 0, 0, 0, 0, 500, 0, 0);
                    Pass2(csStatic, srvLA[0], srvLB[0], uavStatic, G(bw[0]), G(bh[0]));
                    SetCb(lw[0], lh[0], bw[0], bh[0], bw[1], bh[1], Rr, Lam, 1, 0, 500, 0, 1, NoPrev ? 0 : 1);
                    Pass5(csSearch, srvLA[0], srvLB[0], srvFinal[1], srvStatic, srvPrev, uavVec[0], bw[0], bh[0]);
                }
                else if (l == 0)
                {
                    SetCb(lw[0], lh[0], bw[0], bh[0], bw[1], bh[1], Rr, Lam, 1, 0, 500, 0, 0, NoPrev ? 0 : 1);
                    Pass5(csSearch, srvLA[0], srvLB[0], srvFinal[1], srvStatic, srvPrev, uavVec[0], bw[0], bh[0]);
                }
                else
                {
                    SetCb(lw[l], lh[l], bw[l], bh[l], bw[l + 1], bh[l + 1], Rr, Lam, 1, 0);
                    Pass3(csSearch, srvLA[l], srvLB[l], srvFinal[l + 1], uavVec[l], bw[l], bh[l]);
                }
                MaybeMedian(l);
            }

            srvFinal1 = srvFinal[1];

            if (OccEnabled && LastDiff > 4000)
            {
                SetCb(lw[L - 1], lh[L - 1], bw[L - 1], bh[L - 1], 0, 0, R0, Lam, 0, 0);
                Pass2(csSearch, srvLB[L - 1], srvLA[L - 1], uavVecB[L - 1], bw[L - 1], bh[L - 1]);
                srvFinalB[L - 1] = srvVecB[L - 1];
                for (int l = L - 2; l >= 1; l--)
                {
                    SetCb(lw[l], lh[l], bw[l], bh[l], bw[l + 1], bh[l + 1], Rr, Lam, 1, 0);
                    Pass3(csSearch, srvLB[l], srvLA[l], srvFinalB[l + 1], uavVecB[l], bw[l], bh[l]);
                    srvFinalB[l] = srvVecB[l];
                }
            }
            OccOn = OccEnabled && LastDiff > 4000;

            SetCb(lw[0], lh[0], bw[0], bh[0], 0, 0, 0, 0, Subpix, 0, 500, 0, StaticSkip ? 1 : 0);
            if (StaticSkip)
                Pass4(csSubpix, srvLA[0], srvLB[0], srvFinal[0], srvStatic, uavVecSub, bw[0], bh[0]);
            else
                Pass3(csSubpix, srvLA[0], srvLB[0], srvFinal[0], uavVecSub, bw[0], bh[0]);

            ctx.CopyResource(texPrev, vecSub);
        }

        string DumpVec(ID3D11Texture2D t, int lv, string name)
        {
            using var st = Own(dev.CreateTexture2D(new Texture2DDescription(Format.R32_SInt, (uint)bw[lv], (uint)bh[lv], 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)));
            ctx.CopyResource(st, t);
            var m = ctx.Map(st, 0, MapMode.Read, MapFlags.None);
            long sx = 0, sy = 0, n = 0;
            for (uint y = 0; y < (uint)bh[lv]; y++)
                for (uint x = 0; x < (uint)bw[lv]; x++)
                {
                    int v = Marshal.ReadInt32(new IntPtr(m.DataPointer.ToInt64() + y * m.RowPitch + x * 4));
                    sx += (short)(v & 0xFFFF); sy += (short)((v >> 16) & 0xFFFF); n++;
                }
            ctx.Unmap(st, 0);
            return $"{name} ур{lv}: ({sx / 32.0 / n:F2}, {sy / 32.0 / n:F2})";
        }

        public string OccStats() => DumpVec(srvFinal[1] != null ? vecTmp[1] : vec[1], 1, "прямой") + " | " +
                                    DumpVec(vecB[1], 1, "обратный");

        public string Bench(int iters = 60)
        {
            var rnd = new Random(7);
            var lum = new byte[W * H];
            for (int i = 0; i < W * H; i++) lum[i] = (byte)rnd.Next(256);

            void Upload(ID3D11Texture2D dst, int shift, bool halfStatic = false)
            {
                using var st = Own(dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)W, (uint)H, 1, 1,
                    BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write)));
                var m = ctx.Map(st, 0, MapMode.Write, MapFlags.None);
                for (int y = 0; y < H; y++)
                {
                    IntPtr row = m.DataPointer + y * (int)m.RowPitch;
                    for (int x = 0; x < W; x++)
                    {
                        int xx = (halfStatic && x >= W / 2) ? x : Math.Max(0, Math.Min(W - 1, x - shift));
                        byte v = lum[y * W + xx];
                        Marshal.WriteByte(row, x * 4 + 0, v);
                        Marshal.WriteByte(row, x * 4 + 1, v);
                        Marshal.WriteByte(row, x * 4 + 2, v);
                        Marshal.WriteByte(row, x * 4 + 3, 255);
                    }
                }
                ctx.Unmap(st, 0);
                ctx.CopyResource(dst, st);
            }

            Upload(texA, 0);
            Upload(texB, 3, true);
            FrameCount = 1; vecValid = true;

            using var stOut = Own(dev.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)W, (uint)H, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)));
            void Wait()
            {
                ctx.CopyResource(stOut, outTex);
                var m = ctx.Map(stOut, 0, MapMode.Read, MapFlags.None);
                ctx.Unmap(stOut, 0);
            }

            for (int i = 0; i < 4; i++) { RecordStage1(srvA, srvB); RecordStage2(); Render(500, false, null); }
            Wait();

            double Measure(int n, bool search)
            {
                double best = 1e9;
                for (int k = 0; k < 5; k++)
                {
                    var swk = Stopwatch.StartNew();
                    for (int i = 0; i < n; i++)
                    {
                        if (search) { RecordStage1(srvA, srvB); RecordStage2(); }
                        else Render(500 + (i % 400), false, null);
                    }
                    Wait();
                    best = Math.Min(best, swk.Elapsed.TotalMilliseconds / n);
                }
                return best;
            }

            double searchOn = Measure(40, true);
            double renderMs = Measure(40, false);
            bool keep = StaticSkip;
            StaticSkip = false;
            double searchOff = Measure(40, true);
            StaticSkip = keep;

            return $"{W}x{H}: поиск {F(searchOn, "F2")} мс (без пропуска статики {F(searchOff, "F2")}), " +
                   $"построение {F(renderMs, "F2")} мс на кадр";
        }

        public string SelfTest(int shift = 4)
        {
            var rnd = new Random(1234);
            var lum = new byte[W * H];
            for (int i = 0; i < W * H; i++) lum[i] = (byte)rnd.Next(256);
            var tmpB = new byte[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int s = 0;
                    for (int k = -2; k <= 2; k++) s += lum[y * W + Math.Max(0, Math.Min(W - 1, x + k))];
                    tmpB[y * W + x] = (byte)(s / 5);
                }
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int s = 0;
                    for (int k = -2; k <= 2; k++) s += tmpB[Math.Max(0, Math.Min(H - 1, y + k)) * W + x];
                    lum[y * W + x] = (byte)(s / 5);
                }

            byte Lum(int x, int y)
            {
                x = Math.Max(0, Math.Min(W - 1, x));
                y = Math.Max(0, Math.Min(H - 1, y));
                return lum[y * W + x];
            }

            void Upload(ID3D11Texture2D dst, bool shifted)
            {
                using var st = Own(dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)W, (uint)H, 1, 1,
                    BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write)));
                var m = ctx.Map(st, 0, MapMode.Write, MapFlags.None);
                for (int y = 0; y < H; y++)
                {
                    IntPtr row = m.DataPointer + y * (int)m.RowPitch;
                    for (int x = 0; x < W; x++)
                    {
                        byte v = shifted ? Lum(x - shift, y) : Lum(x, y);
                        Marshal.WriteByte(row, x * 4 + 0, v);
                        Marshal.WriteByte(row, x * 4 + 1, v);
                        Marshal.WriteByte(row, x * 4 + 2, v);
                        Marshal.WriteByte(row, x * 4 + 3, 255);
                    }
                }
                ctx.Unmap(st, 0);
                ctx.CopyResource(dst, st);
            }

            Upload(texA, false);
            Upload(texB, true);
            FrameCount = 1; pendingCount = 0; pendingHead = 0; vecValid = false; searchPending = false;

            RecordStage1(srvA, srvB);
            RecordStage2();
            ctx.CopyResource(vecShown, vecSub);
            vecValid = true;
            Render(500, false, null);
            ctx.Flush();

            using var stVec = Own(dev.CreateTexture2D(new Texture2DDescription(Format.R32_SInt, (uint)bw[0], (uint)bh[0], 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)));
            ctx.CopyResource(stVec, vecShown);
            var mv = ctx.Map(stVec, 0, MapMode.Read, MapFlags.None);
            double vx = 0, vy = 0; int vn = 0;
            for (int by = 2; by < bh[0] - 2; by++)
                for (int bx = 2; bx < bw[0] - 2; bx++)
                {
                    int raw = Marshal.ReadInt32(mv.DataPointer + by * (int)mv.RowPitch + bx * 4);
                    vx += ((short)(raw & 0xFFFF)) / 32.0;
                    vy += ((short)((raw >> 16) & 0xFFFF)) / 32.0;
                    vn++;
                }
            ctx.Unmap(stVec, 0);

            using var stOut = Own(dev.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)W, (uint)H, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)));
            ctx.CopyResource(stOut, outTex);
            var mo = ctx.Map(stOut, 0, MapMode.Read, MapFlags.None);
            double errHalf = 0, errStill = 0; int n = 0;
            for (int y = 4; y < H - 4; y++)
                for (int x = 4; x < W - 4; x++)
                {
                    double got = Marshal.ReadByte(mo.DataPointer + y * (int)mo.RowPitch + x * 4);
                    double want = 0.5 * Lum(x - shift / 2, y) + 0.5 * Lum(x - shift / 2 + 1, y);
                    double still = Lum(x, y);
                    errHalf += Math.Abs(got - want);
                    errStill += Math.Abs(got - still);
                    n++;
                }
            ctx.Unmap(stOut, 0);

            return $"вектор ср ({F(vx / Math.Max(1, vn), "F2")}, {F(vy / Math.Max(1, vn), "F2")}) при ожидании ({shift}, 0) | " +
                   $"t=0.5: ошибка к сдвигу 2 = {F(errHalf / n, "F2")}, к «без сдвига» = {F(errStill / n, "F2")}";
        }
    }
}

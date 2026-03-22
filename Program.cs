using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// ── ANSI ─────────────────────────────────────────────────────────────────────
static class A
{
    public const string Reset  = "\x1B[0m";
    public const string Gray   = "\x1B[90m";
    public const string Green  = "\x1B[92m";
    public const string Red    = "\x1B[91m";
    public const string White  = "\x1B[97m";
    public const string Yellow = "\x1B[93m";
    public const string Cyan   = "\x1B[96m";
    public const string SelBg  = "\x1B[48;5;238m";
}

// ── Candle ────────────────────────────────────────────────────────────────────
record Candle(long T, double O, double H, double L, double C, double V);

// ── Provider ──────────────────────────────────────────────────────────────────
/// <summary>
/// Describes how to interact with a crypto exchange REST API.
/// <para>
/// <b>SymbolSplit</b>: regex with exactly two capture groups — group 1 is the
/// base asset (e.g. "BTC"), group 2 is the quote asset (e.g. "USDT").
/// Provide a pattern when the exchange does not return <c>baseAsset</c> /
/// <c>quoteAsset</c> fields in its info response.  Set to <c>null</c> when
/// those fields are available (Binance does this via <c>ExchangeInfoUrl</c>).
/// </para>
/// </summary>
record Provider(
    string                            Name,
    Func<string, string, int, string> KlinesUrl,
    Func<string, string>              ExchangeInfoUrl,
    Func<string, string>              DailyKlinesUrl,
    string?                           SymbolSplit);

// ── Main ──────────────────────────────────────────────────────────────────────
static class Program
{
    // ── Providers ─────────────────────────────────────────────────────────────
    static readonly Provider[] Providers =
    [
        new(
            Name:            "Binance",
            KlinesUrl:       (sym, iv, lim) => $"https://api.binance.com/api/v3/klines?symbol={sym}&interval={iv}&limit={lim}",
            ExchangeInfoUrl: sym => $"https://api.binance.com/api/v3/exchangeInfo?symbol={sym}",
            DailyKlinesUrl:  sym => $"https://api.binance.com/api/v3/klines?symbol={sym}&interval=1d&limit=1",
            SymbolSplit:     null),  // Binance returns baseAsset/quoteAsset via ExchangeInfoUrl
        // Add further providers here.  For exchanges that do not expose
        // baseAsset/quoteAsset in their info response, set SymbolSplit to a
        // two-group regex, e.g.: @"^([A-Z]+?)(USDT|USDC|BTC|ETH|EUR|USD)$"
    ];

    static Provider ActiveProvider => Providers[0];

    // config
    static string   Symbol        = "";
    static string   Interval      = "15m";
    const  int      Limit         = 120;
    static int      RefreshSecs   = 20;
    static bool     ShowVol       = false;
    static bool     ShowYAxis     = false;
    static bool     ShowXAxis     = false;
    // StatusPos: 0=Top 1=Right 2=Bottom 3=Left
    static int      StatusPos     = 2; // default: bottom
    static bool     PrintMode     = false;
    static bool     ShowHelp      = false;

    // state
    static List<Candle> AllCandles   = new();
    static List<Candle> Candles      = new();
    static double?       MidnightOpen = null;
    static double?       DayHigh      = null;
    static double?       DayLow       = null;
    static double?       DayClose     = null;
    static double?       DayVol       = null;
    static int           PriceDecimals = 4; // loaded at startup from exchangeInfo tickSize
    static string        BaseAsset     = "";
    static string        QuoteAsset    = "";
    static bool          NeedsRedraw  = true;
    static bool          NeedsClear   = true;
    static int           LastDot      = -1;
    static string        DotChar      = $"{A.Green}•{A.Reset}";
    static int?          SelCandle    = null;
    static int           TopRow       = 1;
    static int           TermW        = 89;
    static int           TermH        = 19;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static Task<bool>?  FetchTask    = null;
    static long         LastFetch    = 0;
    static long         LastDayFetch = 0;
    static string       TitleTicker  = "";

    static void SetTitle() =>
        Console.Write($"\x1B]0;{TitleTicker} {Interval}\x1B\\");

    static bool StatusTop    => StatusPos == 0;
    static bool StatusRight  => StatusPos == 1;
    static bool StatusBottom => StatusPos == 2;
    static bool StatusLeft   => StatusPos == 3;

    static readonly string[] ValidIntervals =
        ["1m","3m","5m","15m","30m","1h","2h","4h","6h","8h","12h","1d","3d","1w","1M"];

    // ── Windows VT processing ─────────────────────────────────────────────────
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int h);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint m);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint m);
    const int  STD_OUTPUT_HANDLE            = -11;
    const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
        

    static void EnableVT()
    {
        var handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(handle, out uint mode))
            SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }

    static void Cleanup()
    {
        Console.Write("\x1B[?25h\x1B[?1049l");
        Console.TreatControlCAsInput = false;
    }

    // ── Entry point ───────────────────────────────────────────────────────────
    static void Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return; }

        // ── Parse args ────────────────────────────────────────────────────────
        var positionals = new List<string>();
        var flags       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs       = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var arg in args)
        {
            var eq = arg.IndexOf('=');
            if (eq > 0)
                pairs[arg[..eq]] = arg[(eq + 1)..];
            else if (arg.StartsWith("-"))
                flags.Add(arg);
            else
                positionals.Add(arg);
        }

        // Help — any position
        if (positionals.Any(p => p.Equals("help", StringComparison.OrdinalIgnoreCase)) ||
            flags.Contains("--help") || flags.Contains("-h"))
        { Usage(); return; }

        // Print mode — positional word or flag, before or after symbol
        string? printWord = positionals.FirstOrDefault(p => p.Equals("print", StringComparison.OrdinalIgnoreCase));
        if (printWord != null) positionals.Remove(printWord);
        PrintMode = printWord != null || flags.Contains("--print") || flags.Contains("-p");

        // Symbol — first remaining positional
        if (positionals.Count == 0) { Usage(); return; }
        Symbol = positionals[0].ToUpper();
        positionals.RemoveAt(0);

        // Interval — next positional if it's a valid interval, else default
        if (positionals.Count > 0 && ValidIntervals.Contains(positionals[0].ToLower()))
        {
            Interval = positionals[0].ToLower();
            positionals.RemoveAt(0);
        }

        if (!ValidIntervals.Contains(Interval))
        {
            Console.Error.WriteLine($"Error: invalid interval '{Interval}'");
            Console.Error.WriteLine();
            Usage();
            return;
        }

        // Flags (no value)
        ShowVol   = flags.Contains("-v")  || flags.Contains("--enable-volume");
        ShowYAxis = flags.Contains("-y")  || flags.Contains("--enable-y-axis");
        ShowXAxis = flags.Contains("-x")  || flags.Contains("--enable-x-axis");

        // Key=value pairs
        string? rfStr = null;
        if (!pairs.TryGetValue("-R", out rfStr)) pairs.TryGetValue("--refresh", out rfStr);
        if (rfStr != null && int.TryParse(rfStr, out var ri))
            RefreshSecs = Math.Max(5, ri);
        string? stStr = null;
        if (!pairs.TryGetValue("--status", out stStr)) pairs.TryGetValue("-s", out stStr);
        if (stStr != null)
            StatusPos = stStr.ToLower() switch
            {
                "top"    or "t" => 0,
                "right"  or "r" => 1,
                "bottom" or "b" => 2,
                "left"   or "l" => 3,
                _ => 2
            };

        string? cwStr = null;
        if (!pairs.TryGetValue("-c", out cwStr)) pairs.TryGetValue("--cols", out cwStr);
        int? resizeW = cwStr != null && int.TryParse(cwStr, out var cwi) ? cwi : null;

        string? rhStr = null;
        if (!pairs.TryGetValue("-r", out rhStr)) pairs.TryGetValue("--rows", out rhStr);
        int? resizeH = rhStr != null && int.TryParse(rhStr, out var rhi) ? rhi : null;

        // Colors
        (int R, int G, int B) bullColor    = (30, 178, 118);
        (int R, int G, int B) bearColor    = (205, 65, 65);

        string? uStr = null; if (!pairs.TryGetValue("-u", out uStr)) pairs.TryGetValue("--bull-color",     out uStr);
        string? dStr = null; if (!pairs.TryGetValue("-d", out dStr)) pairs.TryGetValue("--bear-color",     out dStr);
        if (uStr != null) bullColor = ParseHex(uStr);
        if (dStr != null) bearColor = ParseHex(dStr);

        (int R, int G, int B) volBullColor = Dim(bullColor, 0.6);
        (int R, int G, int B) volBearColor = Dim(bearColor, 0.6);

        string? vuStr = null; if (!pairs.TryGetValue("-vu", out vuStr)) pairs.TryGetValue("--vol-bull-color", out vuStr);
        string? vdStr = null; if (!pairs.TryGetValue("-vd", out vdStr)) pairs.TryGetValue("--vol-bear-color", out vdStr);
        if (vuStr != null) volBullColor = ParseHex(vuStr);
        if (vdStr != null) volBearColor = ParseHex(vdStr);

        SixelRenderer.SetColors(bullColor, bearColor, volBullColor, volBearColor);

        // Validate symbol and load base/quote assets + price precision from exchange info
        try
        {
            var resp = Http.GetStringAsync(ActiveProvider.ExchangeInfoUrl(Symbol)).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(resp);
            var syms = doc.RootElement.GetProperty("symbols");
            bool found = false;
            foreach (var sym in syms.EnumerateArray())
            {
                BaseAsset  = sym.GetProperty("baseAsset").GetString()  ?? Symbol;
                QuoteAsset = sym.GetProperty("quoteAsset").GetString() ?? "";
                foreach (var filter in sym.GetProperty("filters").EnumerateArray())
                    if (filter.GetProperty("filterType").GetString() == "PRICE_FILTER")
                    {
                        string tick = filter.GetProperty("tickSize").GetString()!;
                        int dot = tick.IndexOf('.');
                        if (dot >= 0)
                        {
                            string dec = tick[(dot + 1)..].TrimEnd('0');
                            PriceDecimals = dec.Length > 0 ? dec.Length : 0;
                        }
                    }
                found = true;
                break;
            }
            if (!found) { Console.Error.WriteLine($"Symbol not found: {Symbol}"); return; }
        }
        catch { Console.Error.WriteLine($"{ActiveProvider.Name} unreachable or symbol not found: {Symbol}"); return; }

        // Console setup
        Console.OutputEncoding       = Encoding.UTF8;
        Console.TreatControlCAsInput = true;
        EnableVT();

        // Enter alternate screen FIRST — this hides ALL probe responses
        Console.Write("\x1B[?1049h");
        Console.Write("\x1B[?25l");                                            // hide cursor
        Console.Write("\x1B[2J\x1B[H");                                        // full clear

        TitleTicker = QuoteAsset.Length > 0 ? $"{BaseAsset}/{QuoteAsset}" : Symbol;
        if (resizeW.HasValue || resizeH.HasValue)
        {
            int w = resizeW ?? Console.WindowWidth;
            int h = resizeH ?? Console.WindowHeight;
            Console.Write($"\x1B[8;{h};{w}t");
        }
        SetTitle();
        Console.Out.Flush();

        // ── Sixel capability probe ────────────────────────────────────────────
        // Now safe inside alternate screen — no garbage can escape
        SixelRenderer.Probe();

        try   { Run(); }
        finally { Cleanup(); }
    }

    static (int R, int G, int B) ParseHex(string s)
    {
        s = s.TrimStart('#');
        if (s.Length != 6) return (128, 128, 128);
        int v = Convert.ToInt32(s, 16);
        return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }

    static (int R, int G, int B) Dim((int R, int G, int B) c, double factor) =>
        ((int)(c.R * factor), (int)(c.G * factor), (int)(c.B * factor));

    static void Usage()
    {
        var e = Console.Error;
        e.WriteLine("Usage:  CandlestickSixel <SYMBOL> [interval] [options]");
        e.WriteLine("        CandlestickSixel print <SYMBOL> [interval] [options]");
        e.WriteLine("        CandlestickSixel help");
        e.WriteLine();
        e.WriteLine("SYMBOL    Any valid exchange pair, e.g. BTCUSDT  ETHBTC  SOLUSDT");
        e.WriteLine("interval  Candlestick timeframe (default: 15m)");
        e.WriteLine($"          Values: {string.Join("  ", ValidIntervals)}");
        e.WriteLine();
        e.WriteLine("Options:");
        e.WriteLine("  -v  --enable-volume          Show volume bars");
        e.WriteLine("  -y  --enable-y-axis          Show Y-axis price labels");
        e.WriteLine("  -x  --enable-x-axis          Show X-axis time labels");
        e.WriteLine("  -c= --cols=<n>               Resize terminal to this column width");
        e.WriteLine("  -r= --rows=<n>               Resize terminal to this row height");
        e.WriteLine("  -R= --refresh=<secs>         Data refresh interval (default: 20, min: 5)");
        e.WriteLine("  -s= --status=<pos>           Status bar position: top|t  right|r  bottom|b  left|l (default: bottom)");
        e.WriteLine("  -u= --bull-color=<hex>       Bull candle color (default: #1eb276)");
        e.WriteLine("  -d= --bear-color=<hex>       Bear candle color (default: #cd4141)");
        e.WriteLine("  -vu= --vol-bull-color=<hex>  Volume bull color (default: 40% dimmed bull)");
        e.WriteLine("  -vd= --vol-bear-color=<hex>  Volume bear color (default: 40% dimmed bear)");
        e.WriteLine();
        e.WriteLine("Examples:");
        e.WriteLine("  CandlestickSixel BTCUSDT");
        e.WriteLine("  CandlestickSixel ETHUSDT 1h -v -y");
        e.WriteLine("  CandlestickSixel SOLUSDT 4h --status=right -c=120 -r=30");
        e.WriteLine("  CandlestickSixel print BTCUSDT 1d");
        e.WriteLine("  CandlestickSixel BTCUSDT --bull-color=#b967ff --bear-color=#ff6b6b");
        e.WriteLine();
        e.WriteLine("Interactive keys (once running):");
        e.WriteLine("  q / Ctrl+C / Esc    Quit");
        e.WriteLine("  F1                  Toggle this help overlay");
        e.WriteLine("  Enter               Force data refresh");
        e.WriteLine("  ← / →               Select / navigate candles");
        e.WriteLine("  ↑ / ↓               Cycle timeframe up / down");
        e.WriteLine("  1 3 5               Switch to 1m / 3m / 5m");
        e.WriteLine("  0  f / g            Switch to 30m / 15m");
        e.WriteLine("  2 4 6 8             Switch to 2h / 4h / 6h / 8h");
        e.WriteLine("  h  d  w  m          Switch to 1h / 1d / 1w / 1M");
        e.WriteLine("  v  y  x             Toggle volume / Y-axis / X-axis");
        e.WriteLine("  s                   Cycle status bar position");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────
    static void Run()
    {
        int  prevW      = 0;
        int  prevH      = 0;
        int  prevPanelW = 0;
        long waitUntil = 0;

        while (true)
        {
            // Keys — drain all available before rendering
            while (Console.KeyAvailable)
            {
                HandleKey(Console.ReadKey(true));
            }

            var nowMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nowSec = nowMs / 1000;

            // Kick off data fetch if due
            if (FetchTask == null && (nowSec - LastFetch >= RefreshSecs || AllCandles.Count == 0))
            {
                FetchTask = FetchCandlesAsync(AllCandles.Count == 0 ? Limit : 2);
            }

            // Collect completed fetch
            if (FetchTask is { IsCompleted: true })
            {
                if (FetchTask.Result) NeedsRedraw = true;
                FetchTask = null;
                LastFetch = nowSec;
                waitUntil = nowSec + RefreshSecs;
            }

            // Daily open — fire-and-forget
            if (nowSec - LastDayFetch >= 3600 || MidnightOpen == null)
            {
                LastDayFetch = nowSec;
                _ = FetchDailyOpenAsync();
            }

            if (AllCandles.Count == 0)
            {
                if (StatusRight || StatusLeft)
                {
                    // Bottom-left corner, no line erase
                    Console.Write($"\x1B[{Console.WindowHeight};1H{A.Red}•{A.Reset} Loading...");
                }
                else
                {
                    int loadRow = StatusBottom ? Console.WindowHeight : 1;
                    Console.Write($"\x1B[{loadRow};1H\x1B[2K{A.Red}•{A.Reset} Loading...");
                }
                Thread.Sleep(200);
                continue;
            }

            // Dimensions
            int tw = Console.WindowWidth;
            int th = Console.WindowHeight;
            if (tw != prevW || th != prevH)
            {
                prevW = tw; prevH = th;
                TermW = tw; TermH = th;
                SelCandle   = null;
                NeedsRedraw = true;
                NeedsClear  = true;
            }

            int volH   = 3;

            // Compute sidePanelW first (depends only on AllCandles/day stats)
            int sidePanelW = 0;
            if (StatusRight || StatusLeft)
            {
                var last2 = AllCandles.Count > 0 ? AllCandles.Last() : null;
                if (last2 != null)
                {
                    bool intrad = Interval != "1d" && Interval != "3d" && Interval != "1w" && Interval != "1M";
                    double sO = intrad && MidnightOpen != null ? MidnightOpen.Value : last2.O;
                    double sH = intrad && DayHigh != null ? DayHigh.Value : AllCandles.Max(c => c.H);
                    double sL = intrad && DayLow  != null ? DayLow.Value  : AllCandles.Min(c => c.L);
                    double sC = intrad && DayClose != null ? DayClose.Value : last2.C;
                    double sChg = sC - sO;
                    double sPct = sO > 0 ? sChg / sO * 100 : 0;
                    double sV = intrad && DayVol != null ? DayVol.Value : AllCandles.Sum(c => c.V);
                    string sPctStr = sChg == 0 ? $"{sPct:0.000}%" : $"{sPct:+0.000;-0.000}%";
                    int maxW = new[]
                    {
                        2 + FmtP(sO).Length, 2 + FmtP(sH).Length,
                        2 + FmtP(sL).Length, 2 + FmtP(sC).Length,
                        2 + FmtP(Math.Abs(sChg)).Length,
                        sPctStr.Length,
                        2 + FmtV(sV).Length
                    }.Max();
                    sidePanelW = Math.Max(9, maxW + 1);
                }
            }

            // Two-pass yLblW: first pass ignores yLblW, second uses actual visible slice
            int n0     = Math.Min(AllCandles.Count, TermW - sidePanelW - 1);
            var vis0   = AllCandles.TakeLast(n0).ToList();
            int yLblW  = ShowYAxis && vis0.Count > 0
                ? FmtP(vis0.Max(c => c.H)).Length + 3 : 0;
            // Second pass: recompute with yLblW known
            int n1     = Math.Min(AllCandles.Count, TermW - yLblW - sidePanelW - 1);
            var vis1   = AllCandles.TakeLast(n1).ToList();
            if (ShowYAxis && vis1.Count > 0)
                yLblW  = FmtP(vis1.Max(c => c.H)).Length + 3;
            int xAxisH = StatusBottom ? 0 : 1;
            TopRow     = StatusBottom ? TermH : StatusTop ? 2 : 1;
            int statusRows = StatusTop || StatusBottom ? 1 : 0;
            if (ShowVol)
            {
                int prelimH = Math.Max(5, TermH - statusRows - 3 - (StatusBottom ? 1 : 0) - xAxisH);
                if (prelimH < 15) volH = 2;
            }
            int chartH = Math.Max(5, TermH - statusRows - (ShowVol ? volH : 0) - (StatusBottom && ShowVol ? 1 : 0) - xAxisH);
            // Reserve 1 separator column between chart and side panel
            int n      = Math.Min(AllCandles.Count, TermW - yLblW - sidePanelW);
            Candles    = AllCandles.TakeLast(n).ToList();

            if (sidePanelW != prevPanelW)
            {
                prevPanelW  = sidePanelW;
                NeedsRedraw = true;
                NeedsClear  = true;
            }

            if (NeedsRedraw)
            {
                NeedsRedraw = false;
                DrawFrame(chartH, volH, yLblW, xAxisH, sidePanelW, n);
            }

            // Dot blink
            int newDot = FetchTask != null ? 2 : (int)(nowMs / 500) % 2;
            if (newDot != LastDot)
            {
                LastDot = newDot;
                DotChar = newDot == 2 ? $"{A.Red}•{A.Reset}"
                        : newDot == 0 ? $"{A.Green}•{A.Reset}"
                                      : $"{A.Gray}•{A.Reset}";
                if (StatusBottom)
                    Console.Write($"\x1B[{TopRow};1H{DotChar}");
                else
                    NeedsRedraw = true;
            }

            Thread.Sleep(50);
        }
    }

    static bool SwitchInterval(string interval)
    {
        if (Interval == interval) return false;
        Interval     = interval;
        AllCandles.Clear();
        MidnightOpen = null;
        DayHigh      = null;
        DayLow       = null;
        DayClose     = null;
        DayVol       = null;
        LastFetch    = 0;
        LastDayFetch = 0;
        SelCandle    = null;
        NeedsRedraw  = NeedsClear = true;
        SetTitle();
        Console.Out.Flush();
        return true;
    }

    // ── Key handler ───────────────────────────────────────────────────────────
    static bool HandleKey(ConsoleKeyInfo k)
    {
        if (k.Key == ConsoleKey.Q ||
           (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control)))
        { Cleanup(); Environment.Exit(0); }

        if (k.Key == ConsoleKey.Escape)
        {
            if (ShowHelp)   { ShowHelp = false; NeedsRedraw = NeedsClear = true; return true; }
            if (SelCandle != null)
            {
                SelCandle = null;
                NeedsRedraw = true;
                if (!ShowXAxis && !StatusRight && !StatusLeft) NeedsClear = true;
                return true;
            }
            Cleanup(); Environment.Exit(0);
        }

        if (k.Key == ConsoleKey.F1)
        {
            ShowHelp = !ShowHelp;
            NeedsRedraw = NeedsClear = true;
            return true;
        }

        // Ignore other keys while help is shown
        if (ShowHelp) return false;

        if (k.Key == ConsoleKey.Enter)   { LastFetch = 0; NeedsRedraw = true; return true; }

        switch (char.ToLower(k.KeyChar))
        {
            case 'v': ShowVol     = !ShowVol;     NeedsRedraw = NeedsClear = true; return true;
            case 'y': ShowYAxis   = !ShowYAxis;   NeedsRedraw = NeedsClear = true; return true;
            case 'x': ShowXAxis   = !ShowXAxis;   NeedsRedraw = NeedsClear = true; return true;
            case 's': StatusPos   = (StatusPos + 1) % 4; NeedsRedraw = NeedsClear = true; return true;
            case '1': return SwitchInterval("1m");
            case '3': return SwitchInterval("3m");
            case '5': return SwitchInterval("5m");
            case '0': return SwitchInterval("30m");
            case '2': return SwitchInterval("2h");
            case '4': return SwitchInterval("4h");
            case '6': return SwitchInterval("6h");
            case '8': return SwitchInterval("8h");
            case 'h': return SwitchInterval("1h");
            case 'd': return SwitchInterval("1d");
            case 'w': return SwitchInterval("1w");
            case 'g': return SwitchInterval("15m"); // g = quarter (15m)
            case 'f': return SwitchInterval("15m"); // f = fifteen
            case 'm': return SwitchInterval("1M");
        }

        int n = Candles.Count;
        if (k.Key == ConsoleKey.LeftArrow)
        {
            bool wasNull = SelCandle == null;
            SelCandle   = SelCandle == null ? n - 1 : Math.Max(0, SelCandle.Value - 1);
            NeedsRedraw = true;
            if (wasNull && !ShowXAxis && !StatusRight && !StatusLeft) NeedsClear = true;
            return true;
        }
        if (k.Key == ConsoleKey.RightArrow)
        {
            bool wasNull = SelCandle == null;
            SelCandle   = SelCandle == null ? 0 : Math.Min(n - 1, SelCandle.Value + 1);
            NeedsRedraw = true;
            if (wasNull && !ShowXAxis && !StatusRight && !StatusLeft) NeedsClear = true;
            return true;
        }
        if (k.Key == ConsoleKey.UpArrow || k.Key == ConsoleKey.DownArrow)
        {
            int idx = Array.IndexOf(ValidIntervals, Interval);
            idx = k.Key == ConsoleKey.UpArrow
                ? Math.Min(ValidIntervals.Length - 1, idx + 1)
                : Math.Max(0, idx - 1);
            return SwitchInterval(ValidIntervals[idx]);
        }

        return false;
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────
    static async Task<bool> FetchCandlesAsync(int fetchLimit)
    {
        try
        {
            var url  = ActiveProvider.KlinesUrl(Symbol, Interval, fetchLimit);
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var incoming = doc.RootElement.EnumerateArray().Select(k => new Candle(
                k[0].GetInt64(),
                double.Parse(k[1].GetString()!),
                double.Parse(k[2].GetString()!),
                double.Parse(k[3].GetString()!),
                double.Parse(k[4].GetString()!),
                double.Parse(k[5].GetString()!)
            )).ToList();

            if (incoming.Count == 0) return false;

            if (AllCandles.Count == 0) { AllCandles = incoming; UpdateLiveDayStats(); return true; }

            // Merge by open timestamp — updates existing bars, appends new ones
            var byTime = AllCandles.ToDictionary(c => c.T);
            foreach (var c in incoming) byTime[c.T] = c;
            AllCandles = byTime.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            if (AllCandles.Count > Limit) AllCandles = AllCandles.TakeLast(Limit).ToList();
            UpdateLiveDayStats();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Updates DayHigh, DayLow, DayClose, DayVol from the loaded intraday candles.
    /// Called after every candle fetch so the side panel shows real-time values
    /// without waiting for the hourly FetchDailyOpenAsync.
    /// MidnightOpen is left untouched — it comes only from the daily fetch.
    /// </summary>
    static void UpdateLiveDayStats()
    {
        bool intrad = Interval != "1d" && Interval != "3d" && Interval != "1w" && Interval != "1M";
        if (!intrad || AllCandles.Count == 0) return;

        DayClose = AllCandles.Last().C;
        DayVol   = AllCandles.Sum(c => c.V);

        // Use the broader of the candle range and the daily fetch values
        double h = AllCandles.Max(c => c.H);
        double l = AllCandles.Min(c => c.L);
        DayHigh  = DayHigh.HasValue ? Math.Max(DayHigh.Value, h) : h;
        DayLow   = DayLow.HasValue  ? Math.Min(DayLow.Value,  l) : l;
    }

    static async Task FetchDailyOpenAsync()
    {
        try
        {
            var url  = ActiveProvider.DailyKlinesUrl(Symbol);
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var first = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Undefined)
            {
                MidnightOpen = double.Parse(first[1].GetString()!);
                DayHigh      = double.Parse(first[2].GetString()!);
                DayLow       = double.Parse(first[3].GetString()!);
                DayClose     = double.Parse(first[4].GetString()!);
                DayVol       = double.Parse(first[5].GetString()!);
                NeedsRedraw  = true;
            }
        }
        catch { }
    }

    // ── Rendering helpers ─────────────────────────────────────────────────────
    static int P2hr(double p, double pMin, double pRng, int h) =>
        (int)Math.Round((h * 2 - 1) * (1.0 - (p - pMin) / pRng));

    static string FmtP(double p) => p.ToString($"F{PriceDecimals}");

    static string FmtV(double v) =>
        v >= 1_000_000 ? $"{v / 1_000_000:F2}M" :
        v >= 1_000     ? $"{v / 1_000:F1}K"     :
                         $"{v:F0}";

    // Strip ANSI escapes, return visible char count
    static int VisLen(string s) =>
        Regex.Replace(s, @"\x1B\[[0-9;]*[a-zA-Z]", "").Length;

    // Trim to maxW visible chars, preserving ANSI escapes
    static string AnsiTrim(string s, int maxW)
    {
        var tokens  = Regex.Split(s, @"(\x1B\[[0-9;]*[a-zA-Z])");
        var sb      = new StringBuilder();
        int visible = 0;
        foreach (var tok in tokens)
        {
            if (Regex.IsMatch(tok, @"^\x1B\[")) { sb.Append(tok); continue; }
            foreach (char ch in tok)
            {
                if (visible >= maxW) return sb.ToString();
                sb.Append(ch);
                visible++;
            }
        }
        return sb.ToString();
    }

    // ── Draw frame ────────────────────────────────────────────────────────────
    static void DrawFrame(int chartH, int volH, int yLblW, int xAxisH, int sidePanelW, int n)
    {
        var sb = new StringBuilder(1024 * 16);

        if (!PrintMode)
        {
            if (NeedsClear)
            {
                sb.Append("\x1B[1;1H\x1B[2J");
                NeedsClear = false;
            }
        }

        // ── Price/volume range ────────────────────────────────────────────────
        double pMax = Math.Round(Candles.Max(c => c.H), 4);
        double pMin = Math.Round(Candles.Min(c => c.L), 4);
        double pRng = pMax - pMin > 0 ? pMax - pMin : 1e-9;
        double vMax = Candles.Max(c => c.V);
        if (vMax == 0) vMax = 1;

        int    highRow  = (int)Math.Floor(P2hr(pMax,     pMin, pRng, chartH) / 2.0);
        int    lowRow   = (int)Math.Floor(P2hr(pMin,     pMin, pRng, chartH) / 2.0);
        double curPrice = Candles.Last().C;
        int    curRow   = (int)Math.Floor(P2hr(curPrice, pMin, pRng, chartH) / 2.0);
        string curCol   = curPrice >= Candles.First().O ? A.Green : A.Red;

        // ── Y-axis labels ─────────────────────────────────────────────────────
        var yLabels = new Dictionary<int, string>();
        int step = Math.Max(1, (int)Math.Round(chartH / 6.0));
        for (int r = 0; r < chartH; r++)
            if (r % step == 0 || r == chartH - 1)
                yLabels[r] = FmtP(pMax - pRng * r / (chartH - 1));
        yLabels[highRow] = FmtP(pMax);
        yLabels[lowRow]  = FmtP(pMin);
        yLabels[curRow]  = FmtP(curPrice);

        Candle? sel = SelCandle is int si && si < n ? Candles[si] : null;
        int selHRow = -1, selLRow = -1, selCRow = -1;
        if (sel != null)
        {
            selHRow = (int)Math.Floor(P2hr(sel.H, pMin, pRng, chartH) / 2.0);
            selLRow = (int)Math.Floor(P2hr(sel.L, pMin, pRng, chartH) / 2.0);
            selCRow = (int)Math.Floor(P2hr(sel.C, pMin, pRng, chartH) / 2.0);
            yLabels[selHRow] = FmtP(sel.H);
            yLabels[selLRow] = FmtP(sel.L);
            yLabels[selCRow] = FmtP(sel.C);
        }

        // ── Status line ───────────────────────────────────────────────────────
        var    last    = Candles.Last();
        bool   isIntraday = Interval != "1d" && Interval != "3d" && Interval != "1w" && Interval != "1M";
        double refOpen = MidnightOpen ?? Candles.First().O;
        double change  = last.C - refOpen;
        double chgPct  = refOpen > 0 ? change / refOpen * 100 : 0;
        string chgCol  = change > 0 ? A.Green : change < 0 ? A.Red : A.White;
        string arrow   = change > 0 ? "▲" : change < 0 ? "▼" : "─";
        string chgPctFmt = change == 0 ? $"{chgPct:0.000}%" : $"({chgPct:+0.000;-0.000}%)";
        double cumVol  = Candles.Sum(c => c.V);

        // For intraday intervals use daily O/H/L/C when available
        double statO = isIntraday && MidnightOpen != null ? MidnightOpen.Value : last.O;
        double statH = isIntraday && DayHigh      != null ? DayHigh.Value      : pMax;
        double statL = isIntraday && DayLow       != null ? DayLow.Value       : pMin;
        double statC = isIntraday && DayClose     != null ? DayClose.Value     : last.C;
        double statV = isIntraday && DayVol       != null ? DayVol.Value       : cumVol;

        string statusContent;
        if (sel != null)
        {
            double sc  = sel.C - sel.O;
            double sp  = sel.O > 0 ? sc / sel.O * 100 : 0;
            string sc2 = sc > 0 ? A.Green : sc < 0 ? A.Red : A.White;
            string sa  = sc > 0 ? "▲" : sc < 0 ? "▼" : "─";
            string scPctFmt = sc == 0 ? $"{sp:0.000}%" : $"({sp:+0.000;-0.000}%)";
            statusContent =
                $"{A.Gray}O{A.Reset} {FmtP(sel.O)}" +
                $"  {A.Gray}H{A.Reset} {FmtP(sel.H)}" +
                $"  {A.Gray}L{A.Reset} {FmtP(sel.L)}" +
                $"  {A.Gray}C{A.Reset} {FmtP(sel.C)}" +
                $"  {sc2}{sa} {FmtP(Math.Abs(sc))}" +
                $" {scPctFmt} {A.Reset}" +
                $" {A.Gray}V{A.Reset} {FmtV(sel.V)}";
        }
        else
        {
            statusContent =
                $"{A.Gray}O{A.Reset} {FmtP(statO)}" +
                $"  {A.Gray}H{A.Reset} {FmtP(statH)}" +
                $"  {A.Gray}L{A.Reset} {FmtP(statL)}" +
                $"  {A.Gray}C{A.Reset} {FmtP(statC)}" +
                $"  {chgCol}{arrow} {FmtP(Math.Abs(change))}" +
                $" {chgPctFmt} {A.Reset}" +
                $" {A.Gray}V{A.Reset} {FmtV(statV)}";
        }

        string statusLine = $"{DotChar} {statusContent}{A.Reset}";

        // Condense double-spaces if too wide
        if (VisLen(statusLine) > TermW)
        {
            statusLine = statusLine.Replace("  ", " ");
            statusLine = statusLine.Replace($"{A.Reset} {A.Gray}V", $"{A.Reset}{A.Gray}V");
        }

        string statusTrimmed = AnsiTrim(statusLine, TermW - 1);
        // Pad to TermW-1 not TermW — writing exactly TermW chars at col 1 of the
        // last row wraps the cursor to row termH+1, scrolling the screen up by 1
        // on every frame and progressively hiding more rows at the top of the chart.
        string statusStr     = statusTrimmed + new string(' ', Math.Max(0, TermW - 1 - VisLen(statusTrimmed)));

        // ── Pre-compute side panel lines for inline rendering ─────────────────
        string[] sidePanelLines = Array.Empty<string>();
        int sidePanelStartRow = 0;
        int lineW = 0;
        if ((StatusRight || StatusLeft) && Candles.Count > 0)
        {
            double dispO   = sel != null ? sel.O : statO;
            double dispH   = sel != null ? sel.H : statH;
            double dispL   = sel != null ? sel.L : statL;
            double dispC   = sel != null ? sel.C : statC;
            double dispV   = sel != null ? sel.V : statV;
            double sideRef = sel != null ? sel.O : statO;
            double sideChg = dispC - sideRef;
            double sidePct = sideRef > 0 ? sideChg / sideRef * 100 : 0;
            string sideCol  = sideChg > 0 ? A.Green : sideChg < 0 ? A.Red : A.White;
            string sideArr  = sideChg > 0 ? "▲" : sideChg < 0 ? "▼" : "─";
            string sidePctFmt = sideChg == 0 ? $"{sidePct:0.000}%" : $"{sidePct:+0.000;-0.000}%";
            string closeCol = dispC >= dispO ? A.Green : A.Red;

            var dt  = DateTimeOffset.FromUnixTimeMilliseconds((sel ?? Candles.Last()).T).ToLocalTime();
            string token = BaseAsset.Length > 0 ? BaseAsset : Symbol;
            var raw = new[]
            {
                ("", ""),  // placeholder for header — rendered specially
                (A.Gray + "O" + A.Reset, FmtP(dispO)),
                (A.Gray + "H" + A.Reset, FmtP(dispH)),
                (A.Gray + "L" + A.Reset, FmtP(dispL)),
                (A.Gray + "C" + A.Reset, closeCol + FmtP(dispC) + A.Reset),
                (sideCol + sideArr + A.Reset, sideCol + FmtP(Math.Abs(sideChg)) + A.Reset),
                ("", sideCol + sidePctFmt + A.Reset),
                (A.Gray + "V" + A.Reset, FmtV(dispV)),
                ("", A.Gray + dt.ToString(sidePanelW - 1 >= 10 ? "yyyy-MM-dd" : "yy-MM-dd") + A.Reset),
                (DotChar, A.Gray + dt.ToString("HH:mm") + A.Reset),
            };
            // maxW = widest visible line across O/H/L/C/chg/pct/vol
            // Labeled rows (O H L C ▲ V): "X value" = 2 + value.Length
            // Unlabeled rows (pct): just value.Length
            int maxW = new[]
            {
                2 + FmtP(dispO).Length,
                2 + FmtP(dispH).Length,
                2 + FmtP(dispL).Length,
                2 + FmtP(dispC).Length,
                2 + FmtP(Math.Abs(sideChg)).Length,
                VisLen(sidePctFmt),
                2 + FmtV(dispV).Length,
            }.Max();
            // lineW = maxW: the natural content width. No fudge factors.
            lineW = sidePanelW - 1;
            sidePanelLines = raw.Select(((string lbl, string val) sl, int i) =>
            {
                string content;
                if (i == 0)
                {
                    string iv = Interval;
                    int space = Math.Max(0, lineW - VisLen(token) - VisLen(iv));
                    content = A.White + token + A.Reset + new string(' ', space) + A.Gray + iv + A.Reset;
                }
                else
                {
                    int visLen = sl.lbl == "" ? VisLen(sl.val) : 2 + VisLen(sl.val);
                    string pad = new string(' ', Math.Max(0, lineW - visLen));
                    content = sl.lbl == ""
                        ? pad + sl.val
                        : sl.lbl + " " + pad + sl.val;
                }
                return content;
            }).ToArray();
            sidePanelStartRow = 1;
        }

        // ── Build frame ───────────────────────────────────────────────────────
        // StatusTop in print mode: write status at row 1 before the chart.
        // Otherwise it is written AFTER the Sixel image so it's never
        // overwritten by the DCS (handled inside the rendering block below).
        if (StatusTop && PrintMode)
            sb.Append($"\x1B[1;1H{statusStr}\x1B[2;1H");

        // Y-axis moves to left side when StatusRight
        bool yAxisLeft = ShowYAxis && StatusRight;

        // ── Sixel: chart origin in terminal coordinates ───────────────────────
        int chartCol = yAxisLeft  ? yLblW + 1
                     : StatusLeft ? sidePanelW + 1
                     :              1;
        // Fix 3: chartRow must be ≥ 2.  WT has a consistent alignment/scroll
        // edge case when a Sixel image starts at the absolute top of the
        // alternate screen (row 1), clipping the top 1–2 rows of the image.
        // Starting at row 2 avoids this entirely.
        
        int chartRow = StatusTop ? 2 : 1;
        // ─────────────────────────────────────────────────────────────────────

        // ── Chart area ────────────────────────────────────────────────────────
        if (!PrintMode)
        {
            // Erase from chartRow to end of screen using ESC[J.
            // This clears the previous Sixel frame without any full-screen
            // clear or cursor movement past termH that would cause a scroll.
            sb.Append($"\x1B[{chartRow};1H\x1B[J");

            sb.Append(SixelRenderer.BuildChartSixel(
                Candles, n, chartH,
                chartRow, chartCol,
                termH:     TermH,
                selCandle: SelCandle,
                gridlines: false));

            if (StatusTop)
                sb.Append($"\x1B[1;1H{statusStr}");

            if (ShowYAxis)
            {
                for (int r = 0; r < chartH; r++)
                {
                    bool selCOnly  = sel != null && r == selCRow;
                    bool selHlOnly = sel != null && (r == selHRow || r == selLRow);
                    string lblCol  = selCOnly  ? A.Yellow :
                                     selHlOnly ? A.Cyan   :
                                     (sel == null && r == curRow ? curCol : A.Gray);
                    string lbl     = yLabels.TryGetValue(r, out var l) ? l : "";
                    string sep     = lbl != "" ? (r == highRow ? "┬" : r == lowRow ? "┴" : "┼") : "│";
                    sb.Append(SixelRenderer.YAxisLabel(
                        r, lbl, n, chartRow, yLblW,
                        lblCol, sep, yAxisLeft, chartCol));
                }
            }

            if (StatusRight && sidePanelLines.Length > 0)
            {
                for (int pidx = 0; pidx < sidePanelLines.Length; pidx++)
                {
                    int panelRow = sidePanelStartRow + pidx;
                    int panelCol = TermW - lineW + 1;
                    sb.Append($"\x1B[{panelRow};{panelCol}H{A.Reset}{sidePanelLines[pidx]}{A.Reset}");
                }
            }

            if (StatusLeft && sidePanelLines.Length > 0)
            {
                for (int pidx = 0; pidx < sidePanelLines.Length; pidx++)
                {
                    int panelRow = sidePanelStartRow + pidx;
                    sb.Append($"\x1B[{panelRow};1H{A.Reset}{sidePanelLines[pidx]}{A.Reset}");
                }
            }

            if (StatusBottom)
                sb.Append($"\x1B[{TermH};1H\x1B[2K{statusStr}");

            // Vol pane — Sixel
            if (ShowVol)
            {
                int volRow = chartRow + chartH;
                sb.Append(SixelRenderer.BuildVolSixel(
                    Candles, n, volH, volRow, chartCol, TermH, SelCandle));
            }

            // X-axis time labels — absolute positioning
            bool showXAxisRow = ShowXAxis || (SelCandle != null && !StatusRight && !StatusLeft);
            if (showXAxisRow)
            {
                int xRow = chartRow + chartH + (ShowVol ? volH : 0);
                if (xRow <= TermH)
                {
                    sb.Append($"\x1B[{xRow};{chartCol}H\x1B[2K");

                    Candle? sel2 = SelCandle is int si2 && si2 < n ? Candles[si2] : null;
                    if (sel2 != null && !StatusRight && !StatusLeft)
                    {
                        var dt   = DateTimeOffset.FromUnixTimeMilliseconds(sel2.T).ToLocalTime();
                        string full = dt.ToString("yyyy-MM-dd HH:mm");
                        int pad  = Math.Max(0, (n - full.Length) / 2);
                        sb.Append(new string(' ', pad));
                        sb.Append(A.Yellow).Append(full).Append(A.Reset);
                    }
                    else if (ShowXAxis)
                    {
                        bool isDateOnly = Interval == "1d" || Interval == "3d";
                        bool isMonthly  = Interval == "1w" || Interval == "1M";
                        var today = DateTime.Today;
                        var xLabels = new string[n];
                        string prevDate = "";
                        for (int c = 0; c < n; c++)
                        {
                            var dt      = DateTimeOffset.FromUnixTimeMilliseconds(Candles[c].T).ToLocalTime();
                            string date = dt.ToString("MM/dd");
                            string time = dt.ToString("HH:mm");
                            bool isToday = dt.Date == today;
                            string lbl2;
                            if (isMonthly)       lbl2 = dt.ToString("MM/yy");
                            else if (isDateOnly) lbl2 = date;
                            else
                            {
                                bool dateChanged = date != prevDate;
                                bool showDate    = dateChanged && (!isToday || prevDate != "");
                                lbl2 = showDate ? $"{date} {time}" : time;
                            }
                            xLabels[c] = lbl2;
                            prevDate = date;
                        }
                        int minGap   = 2;
                        var xRow2    = new string[n];
                        int nextAvail = 0;
                        for (int c = 0; c < n; c++)
                        {
                            if (c < nextAvail) continue;
                            string lbl2 = xLabels[c];
                            if (c + lbl2.Length > n) break;
                            xRow2[c]   = lbl2;
                            nextAvail  = c + lbl2.Length + minGap;
                        }
                        for (int c = 0; c < n; c++)
                        {
                            if (xRow2[c] != null)
                            {
                                sb.Append(A.Gray).Append(xRow2[c]).Append(A.Reset);
                                c += xRow2[c].Length - 1;
                            }
                            else sb.Append(' ');
                        }
                    }
                }
            }

            // Park cursor safely at last row
            sb.Append($"\x1B[{TermH};1H");
        }

        // Help overlay — absolute positioning, works in both modes
        if (ShowHelp)
        {
            string W(string s) => $"{A.White}{s}{A.Reset}{A.SelBg}{A.Gray}";
            var box = new[]
            {
                $"╭───────────────────────┬───────────────────────╮",
                $"│  {W("←/→")}  Select candle   │  {W("s")}  Status position   │",
                $"│  {W("↑/↓")}  Cycle interval  │  {W("v")}  Show/Hide Volume  │",
                $"│   {W("↵")}   Force refresh   │  {W("y")}  Show/Hide Y-axis  │",
                $"│  {W("ESC")}  Exit/Deselect   │  {W("x")}  Show/Hide X-axis  │",
                $"├───────────────────────┴───────────────────────┤",
                $"│  Change Timeframe:  {W("1")}→1m  {W("3")}→3m  {W("5")}→5m  {W("0")}→30m   │",
                $"│    {W("2")}→2h  {W("4")}→4h  {W("6")}→6h  {W("8")}→8h  {W("f")}/{W("g")}→15m            │",
                $"│         {W("h")}→1h   {W("d")}→1d   {W("w")}→1w   {W("m")}→1M             │",
                $"╰───────────────────────────────────────────────╯",
            };

            int boxH   = box.Length;
            int boxW   = 49;
            int startR = Math.Max(1, (chartH - boxH) / 2 + (StatusTop ? 1 : 0));
            int startC = Math.Max(1, (TermW - boxW) / 2);

            foreach (var (line, i) in box.Select((l, i) => (l, i)))
                sb.Append($"\x1B[{startR + i};{startC}H{A.SelBg}{A.Gray}{line}{A.Reset}");
        }

        Console.Write(SixelRenderer.SyncBegin + sb + SixelRenderer.SyncEnd);
    }
}static class SixelRenderer
{
    // ── Win32 handles ─────────────────────────────────────────────────────────
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int h);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint m);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint m);

    const int STD_INPUT_HANDLE  = -10;
    const int STD_OUTPUT_HANDLE = -11;
    const uint ENABLE_LINE_INPUT = 0x0002;
    const uint ENABLE_ECHO_INPUT = 0x0004;

    // ── GetCurrentConsoleFontEx — the reliable cell-size source ───────────────
    // Works on both classic conhost and Windows Terminal (which still routes
    // through an OpenConsole host).  dwFontSize.X/Y are the exact pixel
    // dimensions of one character cell at the current font and DPI.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CONSOLE_FONT_INFOEX
    {
        public uint cbSize;
        public uint nFont;
        public short dwFontSizeX;   // cell width  in pixels
        public short dwFontSizeY;   // cell height in pixels
        public uint FontFamily;
        public uint FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetCurrentConsoleFontEx(
        IntPtr hConsoleOutput,
        bool   bMaximumWindow,
        ref CONSOLE_FONT_INFOEX lpConsoleCurrentFontEx);

    // ── ReadFile for raw stdin reads ──────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(
        IntPtr hFile, byte[] buf, uint toRead,
        out uint read, IntPtr overlapped);

    // ─────────────────────────────────────────────────────────────────────────
    // 1.  CAPABILITY PROBE
    // ─────────────────────────────────────────────────────────────────────────

    // ── Fixed candle column geometry ──────────────────────────────────────────
    public const int BarW     = 5;
    public const int WickW    = 1;
    public const int PadLeft  = 2;
    public const int PadRight = 3;
    const        int WickOff  = PadLeft + (BarW - WickW) / 2;  // = 2 + 2 = 4

    public static bool Supported { get; private set; }
    public static int  CellW     { get; private set; } = 10;
    public static int  CellH     { get; private set; } = 10;

    [DllImport("kernel32.dll")] static extern bool FlushConsoleInputBuffer(IntPtr hConsoleInput);

    public static void Probe()
    {
        var hIn = GetStdHandle(STD_INPUT_HANDLE);
        GetConsoleMode(hIn, out uint savedMode);
        SetConsoleMode(hIn, savedMode & ~(ENABLE_ECHO_INPUT | ENABLE_LINE_INPUT));
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
            {
                CellW = 10;
                CellH = 10;
                Supported = true;
                return;
            }
        }
        finally
        {
            SetConsoleMode(hIn, savedMode);
        }

        // Non-WT only
        try
        {
            FlushConsoleInputBuffer(hIn);
            Console.Write("\x1B[c");
            Console.Out.Flush();
            string da1 = DrainUntil('c', 600);
            Supported = Regex.IsMatch(da1, @"[\[?;]4[;c]");
        }
        finally { }
    }
    
    static string DrainUntil(char term, int timeoutMs)
    {
        var  hIn     = GetStdHandle(STD_INPUT_HANDLE);
        var  buf     = new byte[1];
        var  sb      = new StringBuilder(64);
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (ReadFile(hIn, buf, 1, out uint read, IntPtr.Zero) && read == 1)
            {
                char ch = (char)buf[0];
                sb.Append(ch);
                if (ch == term) break;
            }
            else Thread.Sleep(5);
        }
        return sb.ToString();
    }




    // ─────────────────────────────────────────────────────────────────────────
    // 2.  FIXED PALETTE
    // ─────────────────────────────────────────────────────────────────────────
    // Five colours mirroring the ANSI set already used by the char renderer.
    // Index 0 (background) is implicit — no pixel is ever explicitly set to it.
    // The encoder skips index 0 and relies on the DCS P2=0 parameter to fill
    // the canvas background.

    const int PBg    = 0;    // terminal background
    const int PBull  = 1;    // bullish — #23d18b
    const int PBear  = 2;    // bearish — #f14c4c
    const int PSel   = 3;    // selection bg
    const int PGrid  = 4;    // gridlines
    const int PVolBull = 5;  // vol bullish — 10% darker
    const int PVolBear = 6;  // vol bearish — 10% darker
    const int PalN   = 7;

    static (int R, int G, int B)[] Pal =
    [
        (  0,   0,   0),    // PBg
        ( 30, 178, 118),    // PBull
        (205,  65,  65),    // PBear
        ( 48,  48,  48),    // PSel
        ( 38,  38,  38),    // PGrid
        ( 18, 107,  71),    // PVolBull — 60% of default bull
        (123,  39,  39),    // PVolBear — 60% of default bear
    ];

    public static void SetColors(
        (int R, int G, int B) bull,
        (int R, int G, int B) bear,
        (int R, int G, int B) volBull,
        (int R, int G, int B) volBear)
    {
        Pal[PBull]    = bull;
        Pal[PBear]    = bear;
        Pal[PVolBull] = volBull;
        Pal[PVolBear] = volBear;
    }

    /// Sixel palette header: one "#idx;2;r;g;b" entry per non-background colour.
    /// PBg (index 0) is deliberately omitted — P2=0 in the DCS means "unwritten
    /// pixels use the terminal's own background colour", so defining colour 0
    /// as black would override that and produce an opaque black rectangle.
    static string PaletteHeader()
    {
        var sb = new StringBuilder((PalN - 1) * 22);
        for (int i = 1; i < PalN; i++)   // skip index 0
        {
            var (r, g, b) = Pal[i];
            sb.Append($"#{i};2;{r * 100 / 255};{g * 100 / 255};{b * 100 / 255}");
        }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3.  CANDLE PAINTER
    // ─────────────────────────────────────────────────────────────────────────
    // Produces a flat byte array where each element is a palette index (0..PalN-1).
    //
    // Canvas:  imgW = n * CellW  pixels wide
    //          imgH = chartH * CellH  pixels tall, rounded up to multiple of 6
    //                 (Sixel encoding works in bands of exactly 6 pixel rows)
    //
    // Why palette indices instead of ARGB?  The encoder needs to iterate over
    // colours, not pixels.  Storing palette indices directly avoids a colour-
    // map lookup pass later and keeps the array small (byte vs int).

    /// <summary>
    /// Paints candle bodies and wicks into a palette-index pixel canvas.
    /// </summary>
    /// <returns>
    /// (pixels, imgW, imgH) — imgH is already rounded up to a multiple of 6.
    /// </returns>
    public static (byte[] pixels, int imgW, int imgH) PaintCandles(
        IReadOnlyList<Candle> candles,
        int    n,
        int    chartH,
        int?   selCandle,
        bool   gridlines = false)
    {
        // imgW MUST equal n * CellW exactly — that is the pixel budget the
        // terminal reserved for n character columns.  Any other value causes
        // the terminal to scale the image, which introduces blurring.
        // Candle geometry (BarW / GapW) is painted within this budget; the
        // right portion of each ColW slot that isn't filled by a bar is simply
        // left as background.
        int imgW = n * CellW;
        int sixH = (chartH * CellH / 6) * 6;
        int imgH = sixH;
        var px = new byte[imgW * imgH];

        if (n == 0 || candles.Count == 0)
            return (px, imgW, imgH);

        // ── Price range ───────────────────────────────────────────────────────
        double pMax = double.MinValue, pMin = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (candles[i].H > pMax) pMax = candles[i].H;
            if (candles[i].L < pMin) pMin = candles[i].L;
        }
        double pRng = pMax > pMin ? pMax - pMin : 1e-9;

        // Truncate (not Round) so price maps snap to exact pixel rows with no
        // sub-pixel ambiguity.
        // Chart content starts at CellH/2 px from top and ends CellH/2 px from bottom.
        // Top and bottom half-rows are left blank, giving a natural visual margin.
        int halfRow = CellH / 2;
        int PRow(double price) =>
            halfRow + (int)((1.0 - (price - pMin) / pRng) * (imgH - 1 - halfRow));

        void Vfill(int x, int y0, int y1, int w, byte pal)
        {
            y0 = Math.Max(0, y0);
            y1 = Math.Min(imgH - 1, y1);
            int x1 = Math.Max(0, x);
            int x2 = Math.Min(imgW, x + w);
            for (int y = y0; y <= y1; y++)
            {
                int row = y * imgW;
                for (int col = x1; col < x2; col++)
                    px[row + col] = pal;
            }
        }

        if (gridlines)
        {
            int steps = Math.Max(2, chartH / 4);
            for (int s = 0; s <= steps; s++)
            {
                int gy = (int)((double)s / steps * (imgH - 1));
                Vfill(0, gy, gy, imgW, PGrid);
            }
        }

        for (int ci = 0; ci < n; ci++)
        {
            var  c   = candles[ci];
            byte col = c.C >= c.O ? (byte)PBull : (byte)PBear;

            // Candle slot starts at ci * CellW.  Within the slot:
            //   [0..PadLeft)          left gap
            //   [PadLeft..PadLeft+BarW) body
            //   [PadLeft+BarW..CellW)   right gap
            int x   = ci * CellW;
            int bx  = x + PadLeft;          // body left edge
            int wx  = x + WickOff;          // wick x (centred in body)

            if (selCandle == ci)
                Vfill(x, 0, imgH - 1, CellW-1, PSel);

            int yH  = PRow(c.H);
            int yO  = PRow(c.O);
            int yC  = PRow(c.C);
            int yL  = PRow(c.L);
            int yBT = Math.Min(yO, yC);
            int yBB = Math.Max(yO, yC);


            Vfill(wx,  yH,      yBT - 1, WickW, col);  // upper wick
            Vfill(wx,  yBB + 1, yL,      WickW, col);  // lower wick
            Vfill(bx,  yBT,     yBB,     BarW,  col);  // body
        }

        return (px, imgW, imgH);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4.  SIXEL ENCODER
    // ─────────────────────────────────────────────────────────────────────────
    //
    // Sixel anatomy (relevant subset):
    //
    //   DCS P1 ; P2 ; P3 q  <palette_defs>  <data>  ST
    //
    //   Data is a sequence of "bands", each covering 6 pixel rows.
    //   Within a band, one pass is made per active palette colour:
    //
    //     #<idx>              — select colour
    //     <sixel_chars...>    — one char per column: '?' + (6-bit mask)
    //                           bit n set ↔ pixel at row (band*6+n) has this colour
    //     $                   — carriage return: reset column to 0, stay in band
    //
    //   After all colour passes for a band:
    //     -                   — advance to next band (y += 6)
    //
    //   RLE: !<count><char>   compresses repeated characters.
    //   Sixel chars span ASCII 0x3F ('?') .. 0x7E ('~').
    //
    // We skip PBg (index 0); the DCS P2=0 parameter causes the terminal to
    // fill the canvas background implicitly.

    /// <summary>
    /// Encodes a palette-index pixel array to a Sixel data string
    /// (without the DCS/ST envelope — call <see cref="WrapDcs"/> for that).
    /// </summary>
    /// <param name="px">Flat array of palette indices; length = imgW * imgH.</param>
    /// <param name="imgW">Canvas width in pixels.</param>
    /// <param name="imgH">Canvas height in pixels.  MUST be a multiple of 6.</param>
    public static string EncodePixels(byte[] px, int imgW, int imgH)
    {
        // Rough capacity: ~3 bytes per column per band per active colour.
        var sb    = new StringBuilder(imgW * (imgH / 6) * PalN * 3);
        int bands = imgH / 6;

        for (int band = 0; band < bands; band++)
        {
            int  y0         = band * 6;
            bool firstColor = true;

            for (byte pal = 1; pal < PalN; pal++)     // skip PBg = 0
            {
                // ── Fast presence check ───────────────────────────────────────
                // Skip this colour if it doesn't appear anywhere in the band —
                // avoids emitting a full-width run of '?' (zero-mask) characters.
                bool present = false;
                for (int y = y0; y < y0 + 6 && !present; y++)
                {
                    int rowOff = y * imgW;
                    for (int x = 0; x < imgW; x++)
                        if (px[rowOff + x] == pal) { present = true; break; }
                }
                if (!present) continue;

                // '$' carriage-returns to column 0 inside the current band so
                // the next colour overlays the same rows from the left again.
                if (!firstColor) sb.Append('$');
                firstColor = false;

                sb.Append('#').Append(pal);   // select colour

                // ── RLE-compressed column data ────────────────────────────────
                int  run    = 0;
                byte runVal = 255;   // sentinel — not a valid 6-bit mask

                for (int x = 0; x < imgW; x++)
                {
                    byte mask = 0;
                    for (int bit = 0; bit < 6; bit++)
                    {
                        int y = y0 + bit;
                        if (y < imgH && px[y * imgW + x] == pal)
                            mask |= (byte)(1 << bit);
                    }

                    if (mask == runVal)
                    {
                        run++;
                    }
                    else
                    {
                        if (run > 0) EmitRun(sb, run, runVal);
                        run    = 1;
                        runVal = mask;
                    }
                }
                if (run > 0) EmitRun(sb, run, runVal);
            }

            sb.Append('-');   // advance to next sixel band (y += 6)
        }

        return sb.ToString();
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static void EmitRun(StringBuilder sb, int run, byte mask)
    {
        char ch = (char)('?' + mask);     // 0x3F + 6-bit value → 0x3F..0x7E

        // !n<char> saves space when run ≥ 3.  Below that, literal repetition
        // is the same length or shorter once you count the '!' and digit(s).
        if (run >= 3)
            sb.Append('!').Append(run).Append(ch);
        else if (run == 2)
            { sb.Append(ch); sb.Append(ch); }
        else
            sb.Append(ch);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5.  DCS WRAPPER
    // ─────────────────────────────────────────────────────────────────────────
    //
    // DCS parameters:
    //   P1 = 0   pixel aspect ratio — use terminal default (square pixels)
    //   P2 = 0   background colour mode:
    //              0 = unwritten pixels show the terminal's current background
    //   P3 = 0   horizontal grid size — use terminal default
    //
    // DECSDM (?80h / ?80l):
    //   Without DECSDM, a Sixel image that extends to or past the last terminal
    //   row causes the alternate screen to SCROLL, which pushes the top of the
    //   image off-screen (visible symptom: top half of chart clipped).
    //   ESC[?80h enables "Sixel Display Mode" — the image is painted in-place
    //   without scrolling.  ESC[?80l restores default scrolling behaviour.
    //   Terminals that don't support ?80 silently ignore both sequences.

    static string WrapDcs(string body) =>
        $"\x1BP0;1;0q{PaletteHeader()}{body}\x1B\\";

    // ─────────────────────────────────────────────────────────────────────────
    // 6.  SYNCHRONIZED OUTPUT  (flicker prevention)
    // ─────────────────────────────────────────────────────────────────────────
    //
    // Windows Terminal 1.6+ implements XTSMGRAPHICS / DEC Synchronized Update
    // via the private mode ?2026.  When BSU is active, the terminal batches all
    // rendering changes and applies them atomically on ESU receipt.
    //
    // Without BSU, a large Sixel write produces a noticeable "wipe" as the
    // image streams in line-by-line.  With BSU, the screen updates in one shot.
    //
    // Usage in DrawFrame (see integration notes):
    //
    //   Console.Write(SixelRenderer.SyncBegin + sb.ToString() + SixelRenderer.SyncEnd);
    //
    // Terminals that do not support ?2026 silently ignore both sequences.

    public const string SyncBegin = "\x1B[?2026h";
    public const string SyncEnd   = "\x1B[?2026l";

    // ─────────────────────────────────────────────────────────────────────────
    // 7.  HIGH-LEVEL API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the complete escape sequence that replaces the chart row-loop in
    /// <c>DrawFrame</c>.  The returned string:
    ///
    ///   1. Moves the cursor to (<paramref name="chartRow"/>, <paramref name="chartCol"/>).
    ///   2. Emits the Sixel DCS that paints the full chart area in one shot.
    ///   3. Repositions the cursor to (<paramref name="chartRow"/> + <paramref name="chartH"/>, 1)
    ///      so vol-pane and x-axis writes continue in the correct place.
    ///
    /// After the image is emitted, Y-axis labels should be overlaid with
    /// individual <c>ESC[row;colH</c> sequences — see integration notes.
    /// </summary>
    /// <param name="candles">Visible candle list.</param>
    /// <param name="n">Number of candles to paint.</param>
    /// <param name="chartH">Chart height in terminal rows.</param>
    /// <param name="chartRow">1-based terminal row where the chart starts.</param>
    /// <param name="chartCol">1-based terminal column where candle data starts.</param>
    /// <param name="termH">Total terminal height in rows (Console.WindowHeight).</param>
    /// <param name="selCandle">Selected candle index, or null.</param>
    /// <param name="gridlines">Paint subtle horizontal price gridlines.</param>
    public static string BuildChartSixel(
    IReadOnlyList<Candle> candles,
    int  n,
    int  chartH,
    int  chartRow,
    int  chartCol,
    int  termH,
    int? selCandle,
    bool gridlines = false)
{
    int paintH = Math.Max(1, Math.Min(chartH, termH - chartRow));

    var (px, imgW, imgH) = PaintCandles(candles, n, paintH, selCandle, gridlines);
    string body  = EncodePixels(px, imgW, imgH);
    string sixel = WrapDcs(body);

    int nextRow = Math.Min(chartRow + paintH, termH);

    return $"\x1B[{chartRow};{chartCol}H\x1B[J" +
           sixel +
           $"\x1B[{nextRow};1H";
}

    // ─────────────────────────────────────────────────────────────────────────
    // 7b. VOLUME PANE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Paints volume bars into a palette-index pixel canvas.
    /// Each bar is filled from the bottom up, proportional to volume.
    /// Bullish/bearish colour matches the corresponding candle.
    /// </summary>
    public static (byte[] pixels, int imgW, int imgH) PaintVol(
        IReadOnlyList<Candle> candles,
        int  n,
        int  volH,
        int? selCandle)
    {
        int imgW = n * CellW;
        int sixH = (volH * CellH / 6) * 6;
        int imgH = Math.Max(6, sixH);
        var px   = new byte[imgW * imgH];

        if (n == 0 || candles.Count == 0) return (px, imgW, imgH);

        double vMax = 0;
        for (int i = 0; i < n; i++)
            if (candles[i].V > vMax) vMax = candles[i].V;
        if (vMax == 0) vMax = 1;

        void Vfill(int x, int y0, int y1, int w, byte pal)
        {
            y0 = Math.Max(0, y0);
            y1 = Math.Min(imgH - 1, y1);
            int x1 = Math.Max(0, x);
            int x2 = Math.Min(imgW, x + w);
            for (int y = y0; y <= y1; y++)
            {
                int row = y * imgW;
                for (int col = x1; col < x2; col++)
                    px[row + col] = pal;
            }
        }

        for (int ci = 0; ci < n; ci++)
        {
            var  c   = candles[ci];
            byte col = c.C >= c.O ? (byte)PVolBull : (byte)PVolBear;
            int  x   = ci * CellW;
            int  bx  = x + PadLeft;

            if (selCandle == ci)
                Vfill(x, 0, imgH - 1, CellW - 1, PSel);

            // Bar height proportional to volume, fills from bottom up to halfRow offset
            int halfRow = CellH / 2;
            int usableH = imgH - halfRow;   // top halfRow pixels are blank margin
            int barH = (int)Math.Round(c.V / vMax * usableH);
            if (barH == 0) barH = 1;
            Vfill(bx, imgH - barH, imgH - 1, BarW, col);
        }

        return (px, imgW, imgH);
    }

    /// <summary>
    /// Builds the Sixel escape sequence for the volume pane.
    /// </summary>
    public static string BuildVolSixel(
        IReadOnlyList<Candle> candles,
        int n,
        int volH,
        int volRow,
        int chartCol,
        int termH,
        int? selCandle)
    {
        if (volRow > termH) return "";
        var (px, imgW, imgH) = PaintVol(candles, n, volH, selCandle);
        string body  = EncodePixels(px, imgW, imgH);
        string sixel = WrapDcs(body);
        int nextRow  = Math.Min(volRow + volH, termH);
        return $"\x1B[{volRow};{chartCol}H" +
               sixel +
               $"\x1B[{nextRow};1H";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Y-axis labels are placed after the Sixel image using absolute cursor
    // addressing, since the image occupies those cells.  This helper produces the
    // positioned escape for a single Y-axis label.
    //
    // Usage (replace the existing yLabels loop in DrawFrame):
    //
    //   foreach (var (row, lbl) in yLabels)
    //       sb.Append(SixelRenderer.YAxisLabel(row, lbl, n, chartRow, yLblW,
    //                                          lblCol, sep, yAxisLeft, chartCol));

    /// <summary>
    /// Returns an absolute-positioned escape sequence to render one Y-axis label
    /// alongside the Sixel chart image.
    /// </summary>
    public static string YAxisLabel(
        int    row,         // 0-based chart row
        string lbl,         // formatted price string (may be "")
        int    n,           // candle count (chart width in columns)
        int    chartRow,    // 1-based terminal row where chart starts
        int    yLblW,       // Y-axis label column width (from existing code)
        string lblAnsi,     // ANSI colour for the label
        string sepChar,     // separator: "┬", "┴", "┼", "│"
        bool   yAxisLeft,   // true when Y-axis is on the left (StatusRight mode)
        int    chartCol)    // 1-based column where candle data starts
    {
        int termRow = chartRow + row;

        if (yAxisLeft)
        {
            // Writes yLblW-2 chars: padded(yLblW-3) + ' ' + sep
            // chartCol = yLblW+1 provides the 1-col gap between sep and chart
            string padded = lbl.PadLeft(yLblW - 3);
            return $"\x1B[{termRow};1H\x1B[0m{lblAnsi}{padded}\x1B[0m \x1B[90m{sepChar}\x1B[0m";
        }
        else
        {
            // Right of chart: ' ' + sep + ' ' + padded(yLblW-3)
            int labelCol = chartCol + n;
            string padded = lbl.PadLeft(yLblW - 4);
            return $"\x1B[{termRow};{labelCol}H\x1B[0m \x1B[90m{sepChar}\x1B[0m {lblAnsi}{padded}\x1B[0m";
        }
    }
}
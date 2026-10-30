using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GdPicture14;

namespace BatchOcr
{
    internal class Program
    {
        private const int DefaultLogFlushMs = 100;

        // Runtime configuration (command line, environment variables or defaults, see OcrOptions)
        private static OcrOptions _options = null!;
        private static string _inputFolderPath = "";
        private static string _outputFolderPath = "";
        private static string _dictionaryPath = "";
        private static int _dpi;
        private static int _maxParallelism;

        // Lightweight, mostly lock-free stats
        private static readonly OcrStatistics _statistics = new();

        // Non-blocking logging
        private static readonly BlockingCollection<string> _logQueue = new();
        private static CancellationTokenSource? _logCts;
        private static Task? _logTask;

        static int Main(string[] args)
        {
            // Same number format on every machine in the console output.
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

            _options = OcrOptions.Parse(args);
            if (_options.ShowHelp)
            {
                OcrOptions.PrintHelp();
                return 0;
            }
            _inputFolderPath = _options.InputFolder;
            _outputFolderPath = _options.OutputFolder;
            _dictionaryPath = _options.ResourcesFolder ?? "";
            _dpi = _options.Dpi;
            _maxParallelism = _options.Threads;

            // License: the key comes from GDPICTURE_LICENSE_KEY. RegisterKEY must always be called:
            // an empty key turns on evaluation mode (watermarked output). Without the call, every
            // GdPicture feature throws LicenseMissingFeatureException.
            var licenseKey = (Environment.GetEnvironmentVariable("GDPICTURE_LICENSE_KEY") ?? "").Trim();
            bool registered;
            string reason = "RegisterKEY returned false";
            try { registered = new LicenseManager().RegisterKEY(licenseKey); }
            catch (Exception ex) { registered = false; reason = ex.Message; }
            if (!registered && licenseKey.Length > 0)
            {
                Console.WriteLine($"The license key was rejected ({reason}). Falling back to evaluation mode.");
                licenseKey = "";
                new LicenseManager().RegisterKEY("");
            }
            if (licenseKey.Length == 0)
                Console.WriteLine("No license key: running in evaluation mode (watermarked output).\n");

            Console.WriteLine("GdPicture batch OCR - parallel PDF processor");
            Console.WriteLine("========================================\n");

            ValidateEnvironment();

            StartLogger();

            try
            {
                ProcessPdfFilesParallel();
                StopLoggerAndFlush(); // print every per-file line before the summary
                DisplayFinalStatistics();
            }
            finally
            {
                StopLoggerAndFlush();
            }

            return 0;
        }

        static void ValidateEnvironment()
        {
            Console.WriteLine("Verification: environment and folders");
            Console.WriteLine("------------------------------------");

            if (!Directory.Exists(_inputFolderPath))
            {
                Console.WriteLine("FAILED - Input folder not found: " + _inputFolderPath);
                Environment.Exit(1);
            }
            Console.WriteLine("OK - Input folder: " + _inputFolderPath);

            if (string.IsNullOrEmpty(_dictionaryPath) || !Directory.Exists(_dictionaryPath))
            {
                Console.WriteLine("FAILED - OCR resources folder not found: " + (_dictionaryPath.Length > 0 ? _dictionaryPath : "(not set)"));
                Console.WriteLine("         Set --resources or OCR_RESOURCES_PATH to a folder with " + _options.Language.Split('+')[0] + ".traineddata (see README).");
                Environment.Exit(1);
            }
            Console.WriteLine("OK - Dictionary folder: " + _dictionaryPath);

            if (!Directory.Exists(_outputFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(_outputFolderPath);
                    Console.WriteLine("OK - Created output folder: " + _outputFolderPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAILED - Cannot create output folder: " + ex.Message);
                    Environment.Exit(1);
                }
            }
            else
            {
                Console.WriteLine("OK - Output folder exists: " + _outputFolderPath);
            }

            Console.WriteLine($"Configuration: DPI={_dpi}, Threads={_maxParallelism}, Language={_options.Language}");
            Console.WriteLine("");
        }

        static void StartLogger()
        {
            _logCts = new CancellationTokenSource();
            var token = _logCts.Token;
            _logTask = Task.Run(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested || !_logQueue.IsCompleted)
                    {
                        while (_logQueue.TryTake(out var msg, DefaultLogFlushMs))
                        {
                            Console.WriteLine(msg);
                        }
                    }
                }
                catch { /* ignore */ }
            }, token);
        }

        static void StopLoggerAndFlush()
        {
            try
            {
                _logQueue.CompleteAdding();
                _logCts?.Cancel();
                _logTask?.Wait(5000);
            }
            catch { }

            // final flush
            while (_logQueue.TryTake(out var msg))
                Console.WriteLine(msg);
        }

        static void ProcessPdfFilesParallel()
        {
            var pdfFiles = Directory.GetFiles(_inputFolderPath, "*.pdf", SearchOption.TopDirectoryOnly);
            if (pdfFiles.Length == 0)
            {
                Console.WriteLine("No PDF files found in input folder.");
                return;
            }

            _statistics.TotalFilesCount = pdfFiles.Length;
            var swTotal = Stopwatch.StartNew();

            _logQueue.Add($"\nProcessing {pdfFiles.Length} files with {_maxParallelism} threads (DPI={_dpi})");

            // prepare parallel options
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism };

            // reset stats
            _statistics.Reset();

            int processed = 0;
            int total = pdfFiles.Length;

            // use load-balanced partitioner
            var partitioner = Partitioner.Create(pdfFiles, EnumerablePartitionerOptions.NoBuffering);

            // Each worker thread gets a thread-local GdPicturePDF instance to reuse
            Parallel.ForEach(partitioner, parallelOptions,
                localInit: () =>
                {
                    // create and configure once per thread
                    var g = new GdPicturePDF();
                    return g;
                },
                body: (file, loopState, localGdp) =>
                {
                    int index = Interlocked.Increment(ref processed);
                    var sw = Stopwatch.StartNew();

                    var result = ProcessSingleFileWithLocalGdp(localGdp, file, _dpi, _maxParallelism);

                    sw.Stop();

                    if (result.Success)
                    {
                        Interlocked.Increment(ref _statistics.FilesProcessed);
                        _statistics.ProcessingTimes.Add(sw.Elapsed);
                        Interlocked.Add(ref _statistics.TotalPagesProcessed, result.PagesProcessed);
                    }
                    else
                    {
                        Interlocked.Increment(ref _statistics.FilesWithErrors);
                    }

                    // Log minimal line (non-blocking)
                    var name = Path.GetFileName(file).PadRight(40);
                    if (result.Success)
                    {
                        var perPageSeconds = result.PagesProcessed > 0
                            ? sw.Elapsed.TotalSeconds / result.PagesProcessed
                            : sw.Elapsed.TotalSeconds;
                        _logQueue.Add($"[{index}/{total}] {name} OK - {result.PagesProcessed} pages - {sw.Elapsed.TotalSeconds:F2} s file ({perPageSeconds:F2} s/page file)");
                    }
                    else
                        _logQueue.Add($"[{index}/{total}] {name} FAILED - {result.ErrorMessage}");

                    return localGdp;
                },
                localFinally: localGdp =>
                {
                    try { (localGdp as IDisposable)?.Dispose(); } catch { }
                });

            swTotal.Stop();
            _statistics.TotalTimeSpan = swTotal.Elapsed;
        }

        // This function uses a thread-local GdPicture instance provided by caller (no allocations inside loop)
        static OcrResult ProcessSingleFileWithLocalGdp(GdPicturePDF gdp, string path, int dpi, int maxParallelism)
        {
            var r = new OcrResult { Success = false };

            try
            {
                if (!File.Exists(path))
                {
                    r.ErrorMessage = "File not found";
                    return r;
                }

                // Load file (this sets current document in gdp instance)
                var loadStatus = gdp.LoadFromFile(path, false);
                if (loadStatus != GdPictureStatus.OK)
                {
                    // use GetStat only on failures (diagnostic)
                    r.ErrorMessage = "Failed to load PDF: " + SafeGetStat(gdp);
                    return r;
                }

                int pageCount = gdp.GetPageCount();
                if (pageCount <= 0)
                {
                    r.ErrorMessage = "File is empty or invalid";
                    return r;
                }

                // Single multi-page OCR call using OcrPages with controlled internal threading
                // Keep internal OCR threads conservative to avoid engine instability; rely on outer parallelism
                var ocrThreads = 1;
                var ocrStatus = gdp.OcrPages("*", ocrThreads, _options.Language, _dictionaryPath, "", dpi);

                int pagesProcessed = ocrStatus == GdPictureStatus.OK ? pageCount : 0;

                var outPath = Path.Combine(_outputFolderPath, "OCR_" + Path.GetFileName(path));
                var saveStatus = gdp.SaveToFile(outPath, true);

                if (saveStatus == GdPictureStatus.OK)
                {
                    r.Success = true;
                    r.PagesProcessed = pagesProcessed;
                    r.OutputPath = outPath;
                }
                else
                {
                    r.ErrorMessage = "Failed to save OCR'd file: " + SafeGetStat(gdp);
                }
            }
            catch (Exception ex)
            {
                r.ErrorMessage = "Exception: " + ex.Message;
            }

            return r;
        }

        static string SafeGetStat(GdPicturePDF gdp)
        {
            try { return gdp.GetStat().ToString(); } catch { return "(GetStat failed)"; }
        }

        static void DisplayFinalStatistics()
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine("Final Statistics");
            Console.WriteLine("========================================\n");

            Console.WriteLine($"Files processed: {_statistics.FilesProcessed}/{_statistics.TotalFilesCount}");
            Console.WriteLine($"Files with errors: {_statistics.FilesWithErrors}");
            Console.WriteLine($"Total pages processed: {_statistics.TotalPagesProcessed}");

            if (_statistics.FilesProcessed > 0 && _statistics.TotalTimeSpan.TotalSeconds > 0)
            {
                // Use wall-clock total time to avoid overcounting parallel work
                double avgSecondsPerFile = _statistics.TotalTimeSpan.TotalSeconds / _statistics.FilesProcessed;
                double avgSecondsPerPage = _statistics.TotalPagesProcessed > 0
                    ? _statistics.TotalTimeSpan.TotalSeconds / _statistics.TotalPagesProcessed
                    : 0;
                double avgPagesPerFile = (double)_statistics.TotalPagesProcessed / _statistics.FilesProcessed;

                Console.WriteLine($"Average pages per file: {avgPagesPerFile:F1}");
                Console.WriteLine($"Average time per file: {avgSecondsPerFile:F2} s");
                Console.WriteLine($"Average time per page: {avgSecondsPerPage:F2} s");

                // Projection for a larger batch with the same documents and settings (--estimate N)
                if (_options.ProjectionDocuments > 0)
                {
                    var projection = TimeSpan.FromSeconds(avgSecondsPerFile * _options.ProjectionDocuments);
                    Console.WriteLine($"Estimated time for {_options.ProjectionDocuments:N0} documents: {FormatTimeSpan(projection)}");
                }

                double totalMB = Directory.GetFiles(_inputFolderPath, "*.pdf").Sum(f => new FileInfo(f).Length) / (1024.0 * 1024.0);
                double speed = totalMB > 0 ? totalMB / _statistics.TotalTimeSpan.TotalSeconds : 0;
                Console.WriteLine($"Average speed: {speed:F2} MB/s");
            }

            Console.WriteLine("Total time: " + FormatTimeSpan(_statistics.TotalTimeSpan));
            if (_statistics.TotalFilesCount > 0)
            {
                double successRate = (_statistics.FilesProcessed * 100.0) / _statistics.TotalFilesCount;
                Console.WriteLine("Success rate: " + successRate.ToString("F1") + "%");
            }

            Console.WriteLine("\nOutput folder: " + _outputFolderPath);
        }

        static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            if (ts.TotalMinutes >= 1)
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{(int)ts.TotalSeconds}s {ts.Milliseconds}ms";
        }

    }

    // Thread-safe-ish statistics container
    internal class OcrStatistics
    {
        public int TotalFilesCount { get; set; }

        // use atomic fields for Interlocked operations
        public int FilesProcessed;
        public int FilesWithErrors;
        public int TotalPagesProcessed;

        public ConcurrentBag<TimeSpan> ProcessingTimes { get; set; } = new();
        public TimeSpan TotalTimeSpan { get; set; }

        public void Reset()
        {
            ProcessingTimes = new ConcurrentBag<TimeSpan>();
            FilesProcessed = 0;
            FilesWithErrors = 0;
            TotalPagesProcessed = 0;
            TotalTimeSpan = TimeSpan.Zero;
        }
    }

    internal class OcrResult
    {
        public bool Success { get; set; }
        public int PagesProcessed { get; set; }
        public string OutputPath { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }
}

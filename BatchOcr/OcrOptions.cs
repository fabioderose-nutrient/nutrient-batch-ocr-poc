namespace BatchOcr;

/// <summary>
/// Settings for a batch run. Each value comes from the command line, then from an
/// environment variable, then from a default. Relative paths are resolved against
/// the current directory.
/// </summary>
internal sealed class OcrOptions
{
    public const int DefaultDpi = 150;

    public string InputFolder { get; private set; } = "samples";
    public string OutputFolder { get; private set; } = "output";
    public string? ResourcesFolder { get; private set; }
    public string Language { get; private set; } = "eng";
    public int Dpi { get; private set; } = DefaultDpi;
    public int Threads { get; private set; } = Math.Max(1, Environment.ProcessorCount - 1);
    public int ProjectionDocuments { get; private set; } = 10_000;
    public bool ShowHelp { get; private set; }

    public static OcrOptions Parse(string[] args)
    {
        var o = new OcrOptions
        {
            InputFolder = Env("OCR_INPUT_DIR") ?? "samples",
            OutputFolder = Env("OCR_OUTPUT_DIR") ?? "output",
            ResourcesFolder = Env("OCR_RESOURCES_PATH"),
            Language = Env("OCR_LANGUAGE") ?? "eng",
        };

        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--help": case "-h": o.ShowHelp = true; break;
                case "--input": case "-i": o.InputFolder = Next() ?? o.InputFolder; break;
                case "--output": case "-o": o.OutputFolder = Next() ?? o.OutputFolder; break;
                case "--resources": case "-r": o.ResourcesFolder = Next() ?? o.ResourcesFolder; break;
                case "--language": case "-l": o.Language = Next() ?? o.Language; break;
                case "--dpi":
                    if (int.TryParse(Next(), out var dpi)) o.Dpi = Math.Clamp(dpi, 50, 600);
                    break;
                case "--threads": case "-t":
                    if (int.TryParse(Next(), out var t)) o.Threads = Math.Max(1, t);
                    break;
                case "--estimate":
                    if (int.TryParse(Next(), out var n)) o.ProjectionDocuments = Math.Max(0, n);
                    break;
                default:
                    Console.WriteLine("Unknown option: " + args[i]);
                    o.ShowHelp = true;
                    break;
            }
        }

        o.InputFolder = Path.GetFullPath(o.InputFolder);
        o.OutputFolder = Path.GetFullPath(o.OutputFolder);
        o.ResourcesFolder = o.ResourcesFolder is { Length: > 0 } r ? Path.GetFullPath(r) : FindGdPictureResources();
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run --project BatchOcr -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -i, --input DIR       Folder with the PDFs to OCR (env OCR_INPUT_DIR). Default: samples");
        Console.WriteLine("  -o, --output DIR      Folder for the searchable PDFs (env OCR_OUTPUT_DIR). Default: output");
        Console.WriteLine("  -r, --resources DIR   Folder with the .traineddata files (env OCR_RESOURCES_PATH).");
        Console.WriteLine("                        Default: the GdPicture.Resources package in the NuGet cache");
        Console.WriteLine("  -l, --language LANG   OCR language, for example eng or eng+fra (env OCR_LANGUAGE). Default: eng");
        Console.WriteLine("  --dpi N               OCR resolution, lower is faster. Default: " + DefaultDpi);
        Console.WriteLine("  -t, --threads N       Files processed in parallel. Default: processors - 1");
        Console.WriteLine("  --estimate N          Number of documents for the time projection. Default: 10000");
        Console.WriteLine("  -h, --help            Show this help");
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>The GdPicture.Resources NuGet package ships the OCR dictionaries in runtimes/any/native.</summary>
    private static string? FindGdPictureResources()
    {
        var nugetRoot = Env("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var package = Path.Combine(nugetRoot, "gdpicture.resources");
        if (!Directory.Exists(package)) return null;
        return Directory.GetDirectories(package)
            .OrderByDescending(v => v)
            .Select(v => Path.Combine(v, "runtimes", "any", "native"))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "eng.traineddata")));
    }
}
